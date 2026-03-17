using System.Text.Json;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class JsonOutputFormatterTests
{
    private readonly JsonOutputFormatter _formatter = new();

    // ── WorkItem formatting ─────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ProducesValidJson()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        // Must parse without exception
        var doc = JsonDocument.Parse(result);
        doc.ShouldNotBeNull();
    }

    [Fact]
    public void FormatWorkItem_HasRequiredFields()
    {
        var item = CreateWorkItem(123, "My Task", "Active", assignedTo: "dangreen");

        var result = _formatter.FormatWorkItem(item, showDirty: true);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(123);
        root.GetProperty("title").GetString().ShouldBe("My Task");
        root.GetProperty("type").GetString().ShouldBe("Task");
        root.GetProperty("state").GetString().ShouldBe("Active");
        root.GetProperty("assignedTo").GetString().ShouldBe("dangreen");
        root.GetProperty("isDirty").GetBoolean().ShouldBe(false);
        root.GetProperty("isSeed").GetBoolean().ShouldBe(false);
    }

    [Fact]
    public void FormatWorkItem_HandlesNullAssignedTo()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkItem_ShowsDirty_WhenEnabled()
    {
        var item = CreateWorkItem(123, "My Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: true);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("isDirty").GetBoolean().ShouldBe(true);
    }

    [Fact]
    public void FormatWorkItem_HidesDirty_WhenDisabled()
    {
        var item = CreateWorkItem(123, "My Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: false);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("isDirty").GetBoolean().ShouldBe(false);
    }

    [Fact]
    public void FormatWorkItem_HandlesNullParentId()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Tree formatting ─────────────────────────────────────────────

    [Fact]
    public void FormatTree_ProducesValidJson()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        var doc = JsonDocument.Parse(result);
        doc.ShouldNotBeNull();
    }

    [Fact]
    public void FormatTree_HasFocusParentChainAndChildren()
    {
        var parent = CreateWorkItem(1, "Parent", "Active");
        var focus = CreateWorkItem(2, "Focus", "New");
        var child = CreateWorkItem(3, "Child", "New");
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(2);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(1);
        root.GetProperty("children").GetArrayLength().ShouldBe(1);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void FormatTree_IncludesAllChildren_IgnoringMaxChildren()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var children = new WorkItem[]
        {
            CreateWorkItem(2, "Child 1", "New"),
            CreateWorkItem(3, "Child 2", "New"),
            CreateWorkItem(4, "Child 3", "New"),
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), children);

        var result = _formatter.FormatTree(tree, maxChildren: 2, activeId: null);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // JSON formatter includes all children regardless of maxChildren
        root.GetProperty("children").GetArrayLength().ShouldBe(3);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatTree_NestedItems_IncludeParentId()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = new WorkItem
        {
            Id = 3,
            Type = WorkItemType.Task,
            Title = "Child",
            State = "New",
            ParentId = 1,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        var childElement = doc.RootElement.GetProperty("children")[0];
        childElement.GetProperty("parentId").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void FormatTree_NestedItems_NullParentId()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("focus").GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Workspace formatting ────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_ProducesValidJson()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        var doc = JsonDocument.Parse(result);
        doc.ShouldNotBeNull();
    }

    [Fact]
    public void FormatWorkspace_HasRequiredStructure()
    {
        var ctx = CreateWorkItem(1, "Active", "Active");
        var ws = Workspace.Build(ctx, new[] { ctx }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.GetProperty("context").GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatWorkspace_NullContext_WritesNull()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkspace_IncludesStaleSeeds()
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
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.GetProperty("staleSeeds").GetArrayLength().ShouldBe(1);
        root.GetProperty("staleSeeds")[0].GetInt32().ShouldBe(-2);
    }

    // ── Disambiguation ──────────────────────────────────────────────

    [Fact]
    public void FormatDisambiguation_ProducesValidJson()
    {
        var matches = new List<(int Id, string Title)>
        {
            (123, "Item A"),
            (456, "Item B"),
        };

        var result = _formatter.FormatDisambiguation(matches);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("matches").GetArrayLength().ShouldBe(2);
        root.GetProperty("matches")[0].GetProperty("id").GetInt32().ShouldBe(123);
        root.GetProperty("matches")[0].GetProperty("title").GetString().ShouldBe("Item A");
    }

    // ── FieldChange ─────────────────────────────────────────────────

    [Fact]
    public void FormatFieldChange_ProducesValidJson()
    {
        var change = new FieldChange("System.Title", "Old", "New");

        var result = _formatter.FormatFieldChange(change);

        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("field").GetString().ShouldBe("System.Title");
        root.GetProperty("oldValue").GetString().ShouldBe("Old");
        root.GetProperty("newValue").GetString().ShouldBe("New");
    }

    [Fact]
    public void FormatFieldChange_HandlesNulls()
    {
        var change = new FieldChange("System.AssignedTo", null, "dangreen");

        var result = _formatter.FormatFieldChange(change);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("oldValue").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Error/Success ───────────────────────────────────────────────

    [Fact]
    public void FormatError_ProducesValidJson()
    {
        var result = _formatter.FormatError("something broke");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("something broke");
    }

    [Fact]
    public void FormatSuccess_ProducesValidJson()
    {
        var result = _formatter.FormatSuccess("all good");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("message").GetString().ShouldBe("all good");
    }

    // ── FormatHint / FormatInfo ────────────────────────────────────

    [Fact]
    public void FormatHint_ReturnsEmpty()
    {
        var result = _formatter.FormatHint("some hint");

        result.ShouldBe("");
    }

    [Fact]
    public void FormatInfo_ReturnsJsonObject()
    {
        var result = _formatter.FormatInfo("loading items...");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("info").GetString().ShouldBe("loading items...");
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
}
