using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public sealed class IdsOutputFormatterTests
{
    private readonly IdsOutputFormatter _formatter = new();

    private static string[] SplitLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    // ── FormatWorkItem ──────────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ReturnsBareId()
    {
        var item = CreateWorkItem(42, "Some Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldBe("42");
    }

    [Fact]
    public void FormatWorkItem_IgnoresShowDirty()
    {
        var item = CreateWorkItem(42, "Some Task", "Active");
        item.UpdateField("test", "val");

        var result = _formatter.FormatWorkItem(item, showDirty: true);

        result.ShouldBe("42");
        result.ShouldNotContain("*");
    }

    [Fact]
    public void FormatWorkItem_NoTitleOrState()
    {
        var item = CreateWorkItem(999, "Complex Title With Spaces", "In Progress");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldBe("999");
        result.ShouldNotContain("Complex");
        result.ShouldNotContain("In Progress");
    }

    // ── FormatTree ──────────────────────────────────────────────────

    [Fact]
    public void FormatTree_OutputsIdsInDepthFirstOrder()
    {
        var parent = CreateWorkItem(100, "Epic", "Active");
        var focus = CreateWorkItem(200, "Feature", "Active");
        var child1 = CreateWorkItem(301, "Task 1", "New");
        var child2 = CreateWorkItem(302, "Task 2", "New");
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child1, child2 });

        var result = _formatter.FormatTree(tree, maxDepth: 5, activeId: null);
        var lines = SplitLines(result);

        lines.Length.ShouldBe(4);
        lines[0].ShouldBe("100");
        lines[1].ShouldBe("200");
        lines[2].ShouldBe("301");
        lines[3].ShouldBe("302");
    }

    [Fact]
    public void FormatTree_FocusOnly_SingleId()
    {
        var focus = CreateWorkItem(42, "Solo Item", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxDepth: 5, activeId: null);

        result.ShouldBe("42");
    }

    [Fact]
    public void FormatTree_WithDescendants_DepthFirst()
    {
        var focus = CreateWorkItem(1, "Root", "Active");
        var child = CreateWorkItem(10, "Child", "New");
        var grandchild = CreateWorkItem(100, "Grandchild", "New");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { grandchild }
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        var result = _formatter.FormatTree(tree, maxDepth: 5, activeId: null);
        var lines = SplitLines(result);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("1");
        lines[1].ShouldBe("10");
        lines[2].ShouldBe("100");
    }

    [Fact]
    public void FormatTree_RespectsMaxDepth()
    {
        var focus = CreateWorkItem(1, "Root", "Active");
        var child = CreateWorkItem(10, "Child", "New");
        var grandchild = CreateWorkItem(100, "Grandchild", "New");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { grandchild }
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        // maxDepth=1 means only the focused item's direct children (depth 1 from focus)
        var result = _formatter.FormatTree(tree, maxDepth: 1, activeId: null);
        var lines = SplitLines(result);

        lines.Length.ShouldBe(2);
        lines[0].ShouldBe("1");
        lines[1].ShouldBe("10");
    }

    [Fact]
    public void FormatTree_NoAnsiCodes()
    {
        var focus = CreateWorkItem(1, "Root", "Active");
        var child = CreateWorkItem(2, "Child", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxDepth: 5, activeId: null);

        result.ShouldNotContain("\x1b[");
        result.ShouldNotContain("[Active]");
        result.ShouldNotContain("Root");
    }

    [Fact]
    public void FormatTree_OneIdPerLine()
    {
        var parent = CreateWorkItem(1, "P", "Active");
        var focus = CreateWorkItem(2, "F", "Active");
        var c1 = CreateWorkItem(3, "C1", "New");
        var c2 = CreateWorkItem(4, "C2", "New");
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { c1, c2 });

        var result = _formatter.FormatTree(tree, maxDepth: 5, activeId: null);
        var lines = SplitLines(result);

        foreach (var line in lines)
        {
            int.TryParse(line.Trim(), out var parsed).ShouldBeTrue($"Line '{line}' is not a bare integer");
            parsed.ShouldBeGreaterThan(0);
        }
    }

    // ── FormatWorkspace ─────────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_OutputsAllSprintItemIds()
    {
        var item1 = CreateWorkItem(10, "Task A", "Active");
        var item2 = CreateWorkItem(20, "Task B", "New");
        var item3 = CreateWorkItem(30, "Task C", "Done");
        var ws = Workspace.Build(item1, new[] { item1, item2, item3 }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var lines = SplitLines(result);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("10");
        lines[1].ShouldBe("20");
        lines[2].ShouldBe("30");
    }

    [Fact]
    public void FormatWorkspace_EmptySprintItems_ReturnsEmpty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatWorkspace_IgnoresSeeds()
    {
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Seed", State = "",
            IsSeed = true, SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatWorkspace_NoAnsi()
    {
        var item = CreateWorkItem(42, "Task", "Active");
        var ws = Workspace.Build(item, new[] { item }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldNotContain("\x1b[");
        result.ShouldNotContain("CTX");
        result.ShouldNotContain("SPR");
        result.ShouldNotContain("Task");
    }

    [Fact]
    public void FormatWorkspace_OneIdPerLine()
    {
        var items = new[]
        {
            CreateWorkItem(1, "A", "Active"),
            CreateWorkItem(2, "B", "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var lines = SplitLines(result);

        foreach (var line in lines)
        {
            int.TryParse(line.Trim(), out _).ShouldBeTrue($"Line '{line}' is not a bare integer");
        }
    }

    // ── FormatSprintView (alias) ────────────────────────────────────

    [Fact]
    public void FormatSprintView_MatchesFormatWorkspace()
    {
        var items = new[] { CreateWorkItem(10, "Task", "Active"), CreateWorkItem(20, "Task 2", "New") };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var workspaceResult = _formatter.FormatWorkspace(ws, staleDays: 14);
        var sprintResult = _formatter.FormatSprintView(ws, staleDays: 14);

        sprintResult.ShouldBe(workspaceResult);
    }

    // ── FormatQueryResults ──────────────────────────────────────────

    [Fact]
    public void FormatQueryResults_OutputsIdsFromResults()
    {
        var items = new[]
        {
            CreateWorkItem(101, "Bug A", "Active"),
            CreateWorkItem(202, "Bug B", "Resolved"),
            CreateWorkItem(303, "Bug C", "New"),
        };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var lines = SplitLines(output);

        lines.Length.ShouldBe(3);
        lines[0].ShouldBe("101");
        lines[1].ShouldBe("202");
        lines[2].ShouldBe("303");
    }

    [Fact]
    public void FormatQueryResults_EmptyResults_ReturnsEmpty()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatQueryResults_TruncatedFlag_NoEffect()
    {
        var items = new[] { CreateWorkItem(42, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: true);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldBe("42");
    }

    // ── Non-list methods return empty ───────────────────────────────

    [Fact]
    public void NonListMethods_ReturnEmpty()
    {
        // All non-list format methods should return empty string
        _formatter.FormatError("error").ShouldBe(string.Empty);
        _formatter.FormatSuccess("ok").ShouldBe(string.Empty);
        _formatter.FormatHint("hint").ShouldBe(string.Empty);
        _formatter.FormatInfo("info").ShouldBe(string.Empty);
        _formatter.FormatBranchInfo("main").ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatStatusSummary_ReturnsEmpty()
    {
        var item = CreateWorkItem(1, "Test", "Active");
        _formatter.FormatStatusSummary(item).ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatSetConfirmation_ReturnsBareId()
    {
        var item = CreateWorkItem(42, "Task", "Active");
        _formatter.FormatSetConfirmation(item).ShouldBe("42");
    }

    [Fact]
    public void FormatFieldChange_ReturnsEmpty()
    {
        var change = new FieldChange("System.Title", "Old", "New");
        _formatter.FormatFieldChange(change).ShouldBe(string.Empty);
    }

    [Fact]
    public void FormatDisambiguation_ReturnsEmpty()
    {
        var matches = new List<(int Id, string Title)> { (1, "A"), (2, "B") };
        _formatter.FormatDisambiguation(matches).ShouldBe(string.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, string state)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
