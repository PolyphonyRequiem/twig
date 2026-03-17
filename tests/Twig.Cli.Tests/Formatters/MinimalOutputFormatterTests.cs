using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class MinimalOutputFormatterTests
{
    private readonly MinimalOutputFormatter _formatter = new();

    // ── WorkItem formatting ─────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_SingleLine()
    {
        var item = CreateWorkItem(123, "My Task", "Active", assignedTo: "dangreen");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("\n");
        result.ShouldBe("#123 Active \"My Task\" Task @dangreen");
    }

    [Fact]
    public void FormatWorkItem_NoAnsi()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("\x1b[");
    }

    [Fact]
    public void FormatWorkItem_ShowsDirtyMarker()
    {
        var item = CreateWorkItem(123, "My Task", "Active");
        item.UpdateField("test", "val");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: true);

        result.ShouldContain(" *");
    }

    [Fact]
    public void FormatWorkItem_HidesDirtyMarker_WhenDisabled()
    {
        var item = CreateWorkItem(123, "My Task", "Active");
        item.UpdateField("test", "val");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain(" *");
    }

    [Fact]
    public void FormatWorkItem_NoAssigned_OmitsAt()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("@");
    }

    // ── Tree formatting ─────────────────────────────────────────────

    [Fact]
    public void FormatTree_SingleLinePerItem()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "Child", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var lines = result.Split('\n');

        // Focus line + child line
        lines.Length.ShouldBe(2);
    }

    [Fact]
    public void FormatTree_NoAnsi()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldNotContain("\x1b[");
    }

    [Fact]
    public void FormatTree_FocusMarkedWithGreaterThan()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldStartWith("> ");
    }

    [Fact]
    public void FormatTree_ChildrenIndented()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "Child", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var lines = result.Split('\n');

        lines[1].ShouldStartWith("  ");
        lines[1].ShouldContain("#2");
    }

    [Fact]
    public void FormatTree_ShowsShorthandState()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("[c]"); // Active -> c
    }

    [Fact]
    public void FormatTree_CollapsesExcessChildren()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var children = new[]
        {
            CreateWorkItem(2, "C1", "New"),
            CreateWorkItem(3, "C2", "New"),
            CreateWorkItem(4, "C3", "New"),
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), children);

        var result = _formatter.FormatTree(tree, maxChildren: 1, activeId: null);

        result.ShouldContain("+2 more");
    }

    // ── Workspace formatting ────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_UsesSectionPrefixes()
    {
        var ctx = CreateWorkItem(1, "Active Item", "Active");
        var sprint = new[] { ctx };
        var ws = Workspace.Build(ctx, sprint, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("CTX ");
        result.ShouldContain("SPR ");
    }

    [Fact]
    public void FormatWorkspace_ShowsSeedPrefix()
    {
        var seed = CreateSeed(-1, "Seed Task");
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("SEED ");
        result.ShouldContain("CTX (none)");
    }

    [Fact]
    public void FormatWorkspace_NoAnsi()
    {
        var ctx = CreateWorkItem(1, "Active Item", "Active");
        var ws = Workspace.Build(ctx, new[] { ctx }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldNotContain("\x1b[");
    }

    [Fact]
    public void FormatWorkspace_StaleMarker()
    {
        var staleSeed = new WorkItem
        {
            Id = -2,
            Type = WorkItemType.Task,
            Title = "Stale Seed",
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { staleSeed });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("STALE");
    }

    // ── Disambiguation ──────────────────────────────────────────────

    [Fact]
    public void FormatDisambiguation_OneLinePerMatch()
    {
        var matches = new List<(int Id, string Title)>
        {
            (123, "Item A"),
            (456, "Item B"),
        };

        var result = _formatter.FormatDisambiguation(matches);
        var lines = result.Split('\n');

        lines.Length.ShouldBe(2);
        lines[0].ShouldContain("#123");
        lines[0].ShouldContain("\"Item A\"");
        lines[1].ShouldContain("#456");
    }

    [Fact]
    public void FormatDisambiguation_NoAnsi()
    {
        var matches = new List<(int Id, string Title)> { (1, "X") };

        var result = _formatter.FormatDisambiguation(matches);

        result.ShouldNotContain("\x1b[");
    }

    // ── FieldChange ─────────────────────────────────────────────────

    [Fact]
    public void FormatFieldChange_PlainText()
    {
        var change = new FieldChange("System.Title", "Old", "New");

        var result = _formatter.FormatFieldChange(change);

        result.ShouldBe("System.Title: Old -> New");
    }

    // ── Error/Success ───────────────────────────────────────────────

    [Fact]
    public void FormatError_PlainText()
    {
        var result = _formatter.FormatError("something broke");

        result.ShouldBe("error: something broke");
        result.ShouldNotContain("\x1b[");
    }

    [Fact]
    public void FormatSuccess_PlainText()
    {
        var result = _formatter.FormatSuccess("done");

        result.ShouldBe("done");
        result.ShouldNotContain("\x1b[");
    }

    // ── FormatHint / FormatInfo ────────────────────────────────────

    [Fact]
    public void FormatHint_ReturnsEmpty()
    {
        var result = _formatter.FormatHint("some hint");

        result.ShouldBe("");
    }

    [Fact]
    public void FormatInfo_ReturnsRawMessage()
    {
        var result = _formatter.FormatInfo("loading items...");

        result.ShouldBe("loading items...");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, string state, string? assignedTo = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateSeed(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
