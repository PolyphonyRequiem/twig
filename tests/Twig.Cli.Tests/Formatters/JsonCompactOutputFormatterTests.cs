using System.Text.Json;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class JsonCompactOutputFormatterTests
{
    private readonly JsonCompactOutputFormatter _formatter = new(new JsonOutputFormatter());

    // ── WorkItem formatting ─────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ProducesValidJson()
    {
        var item = CreateWorkItem(42, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        JsonDocument.Parse(result).ShouldNotBeNull();
    }

    [Fact]
    public void FormatWorkItem_HasOnlyCompactFields()
    {
        var item = CreateWorkItem(42, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);
        var root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("My Task");
        root.GetProperty("type").GetString().ShouldBe("Task");
        root.GetProperty("state").GetString().ShouldBe("Active");

        // Should NOT contain full-format fields
        root.TryGetProperty("assignedTo", out _).ShouldBeFalse();
        root.TryGetProperty("isSeed", out _).ShouldBeFalse();
        root.TryGetProperty("iterationPath", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkItem_OmitsIsDirty_WhenClean()
    {
        var item = CreateWorkItem(42, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: true);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("isDirty", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkItem_IncludesIsDirty_WhenDirty()
    {
        var item = CreateWorkItem(42, "My Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: true);
        var root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("isDirty").GetBoolean().ShouldBeTrue();
    }

    // ── Tree formatting ──────────────────────────────────────────────

    [Fact]
    public void FormatTree_ProducesValidJson()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        JsonDocument.Parse(result).ShouldNotBeNull();
    }

    [Fact]
    public void FormatTree_HasCompactFocusAndChildren()
    {
        var parent = CreateWorkItem(1, "Parent", "Active");
        var focus = CreateWorkItem(2, "Focus", "New");
        var child = CreateWorkItem(3, "Child", "New");
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(2);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(1);
        root.GetProperty("children").GetArrayLength().ShouldBe(1);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(1);

        // Children should have compact schema only
        var childElem = root.GetProperty("children")[0];
        childElem.GetProperty("id").GetInt32().ShouldBe(3);
        childElem.TryGetProperty("assignedTo", out _).ShouldBeFalse();
    }

    // ── Workspace formatting ─────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_ProducesValidJson()
    {
        var item = CreateWorkItem(1, "Sprint Item", "Active");
        var ws = Workspace.Build(item, new[] { item }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);

        JsonDocument.Parse(result).ShouldNotBeNull();
    }

    [Fact]
    public void FormatWorkspace_HasCompactSchema()
    {
        var context = CreateWorkItem(1, "Context", "Active");
        var sprint = CreateWorkItem(2, "Sprint", "New");
        var ws = Workspace.Build(context, new[] { sprint }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);
        var root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("context").GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(0);

        // Sprint items should use compact schema
        var sprintElem = root.GetProperty("sprintItems")[0];
        sprintElem.GetProperty("id").GetInt32().ShouldBe(2);
        sprintElem.TryGetProperty("assignedTo", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkspace_WithSections_IncludesSectionsAndExcludedIds()
    {
        var sprintItems = new[] { CreateWorkItem(1, "Sprint Task", "Active") };
        var manualItems = new[] { CreateWorkItem(2, "Manual Task", "New") };
        var sections = WorkspaceSections.Build(sprintItems, manualItems: manualItems, excludedIds: new[] { 42 });
        var ws = Workspace.Build(null, sprintItems, Array.Empty<WorkItem>(), sections: sections);

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("sections", out var sectionsEl).ShouldBeTrue();
        sectionsEl.GetArrayLength().ShouldBe(2);

        sectionsEl[0].GetProperty("modeName").GetString().ShouldBe("Sprint");
        sectionsEl[0].GetProperty("itemCount").GetInt32().ShouldBe(1);
        sectionsEl[0].GetProperty("itemIds")[0].GetInt32().ShouldBe(1);

        sectionsEl[1].GetProperty("modeName").GetString().ShouldBe("Manual");
        sectionsEl[1].GetProperty("itemCount").GetInt32().ShouldBe(1);
        sectionsEl[1].GetProperty("itemIds")[0].GetInt32().ShouldBe(2);

        root.TryGetProperty("excludedItemIds", out var excluded).ShouldBeTrue();
        excluded.GetArrayLength().ShouldBe(1);
        excluded[0].GetInt32().ShouldBe(42);
    }

    [Fact]
    public void FormatWorkspace_WithoutSections_OmitsSectionsProperty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("sections", out _).ShouldBeFalse();
        root.TryGetProperty("excludedItemIds", out _).ShouldBeFalse();
    }

    // ── Delegated methods ────────────────────────────────────────────

    [Fact]
    public void FormatError_DelegatesToFullFormatter()
    {
        var result = _formatter.FormatError("something failed");

        var root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().ShouldBe("something failed");
    }

    [Fact]
    public void FormatHint_DelegatesToFullFormatter()
    {
        var result = _formatter.FormatHint("try this");

        // JSON formatter suppresses hints (returns empty string)
        result.ShouldBeEmpty();
    }

    // ── FormatQueryResults (Task 3.4) ───────────────────────────────

    [Fact]
    public void FormatQueryResults_ProducesValidJsonArray()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        var doc = JsonDocument.Parse(output);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public void FormatQueryResults_ZeroResults_ReturnsEmptyArray()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldBe("[]");
    }

    [Fact]
    public void FormatQueryResults_IsCompact_NoIndentation()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldNotContain("\n");
        output.ShouldNotContain("  ");
    }

    [Fact]
    public void FormatQueryResults_ItemsHaveCompactFields()
    {
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Issue,
            Title = "MCP server integration",
            State = "Doing",
            AssignedTo = "Daniel Green",
            AreaPath = AreaPath.Parse("Twig").Value,
            IterationPath = IterationPath.Parse("Twig\\Sprint 1").Value,
        };
        var result = new QueryResult(new[] { item }, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;
        var itemEl = root[0];

        itemEl.GetProperty("id").GetInt32().ShouldBe(42);
        itemEl.GetProperty("title").GetString().ShouldBe("MCP server integration");
        itemEl.GetProperty("type").GetString().ShouldBe("Issue");
        itemEl.GetProperty("state").GetString().ShouldBe("Doing");
        itemEl.GetProperty("assignedTo").GetString().ShouldBe("Daniel Green");
    }

    [Fact]
    public void FormatQueryResults_OmitsMetadataFields()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: false, Query: "test");

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        // Should be a flat array, not an object with query/count/truncated
        root.ValueKind.ShouldBe(JsonValueKind.Array);

        // Items should not contain areaPath/iterationPath (full-format fields)
        var itemEl = root[0];
        itemEl.TryGetProperty("areaPath", out _).ShouldBeFalse();
        itemEl.TryGetProperty("iterationPath", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatQueryResults_NullAssignedTo_WritesNull()
    {
        var item = CreateWorkItem(1, "Unassigned Task", "New");
        var result = new QueryResult(new[] { item }, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root[0].GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatQueryResults_MultipleItems_AllPresent()
    {
        var items = new[]
        {
            CreateWorkItem(1, "First", "New"),
            CreateWorkItem(2, "Second", "Active"),
            CreateWorkItem(3, "Third", "Closed"),
        };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetArrayLength().ShouldBe(3);
        root[0].GetProperty("id").GetInt32().ShouldBe(1);
        root[1].GetProperty("id").GetInt32().ShouldBe(2);
        root[2].GetProperty("id").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatWorkspace_WithTrackedItems_IncludesCompactTrackedArray()
    {
        var items = new[] { CreateWorkItem(1, "Sprint Item", "Active") };
        var tracked = new[] {
            new TrackedItem(1, TrackingMode.Single, DateTimeOffset.UtcNow),
            new TrackedItem(2, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>(),
            trackedItems: tracked);

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("trackedItems", out var arr).ShouldBeTrue();
        arr.GetArrayLength().ShouldBe(2);
        arr[0].GetProperty("workItemId").GetInt32().ShouldBe(1);
        arr[0].GetProperty("mode").GetString().ShouldBe("Single");
        arr[1].GetProperty("workItemId").GetInt32().ShouldBe(2);
        arr[1].GetProperty("mode").GetString().ShouldBe("Tree");
        // Compact schema omits trackedAt
        arr[0].TryGetProperty("trackedAt", out _).ShouldBeFalse();
        arr[1].TryGetProperty("trackedAt", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkspace_WithEmptyTrackedItems_OmitsTrackedItemsProperty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 7);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("trackedItems", out _).ShouldBeFalse();
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

    // ── Recursive tree output (Task 2073) ───────────────────────────

    [Fact]
    public void FormatTree_WithDescendants_IncludesNestedChildren()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItemWithParent(2, "Child", "New", 1);
        var grandchild = CreateWorkItemWithParent(3, "Grandchild", "New", 2);

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [2] = new[] { grandchild }
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        var childElement = doc.RootElement.GetProperty("children")[0];
        childElement.GetProperty("id").GetInt32().ShouldBe(2);
        childElement.GetProperty("children").GetArrayLength().ShouldBe(1);
        childElement.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatTree_LeafChild_HasEmptyChildrenArray()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItemWithParent(2, "Leaf", "New", 1);
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        var childElement = doc.RootElement.GetProperty("children")[0];
        childElement.GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    private static WorkItem CreateWorkItemWithParent(int id, string title, string state, int parentId)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
