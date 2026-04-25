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

    [Fact]
    public void FormatWorkspace_WithSections_IncludesSectionsMetadata()
    {
        var sprintItems = new[] { CreateWorkItem(1, "Sprint Task", "Active") };
        var manualItems = new[] { CreateWorkItem(2, "Manual Task", "New") };
        var sections = WorkspaceSections.Build(sprintItems, manualItems: manualItems);
        var ws = Workspace.Build(null, sprintItems, Array.Empty<WorkItem>(), sections: sections);

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("sections", out var sectionsEl).ShouldBeTrue();
        sectionsEl.GetArrayLength().ShouldBe(2);

        sectionsEl[0].GetProperty("modeName").GetString().ShouldBe("Sprint");
        sectionsEl[0].GetProperty("itemCount").GetInt32().ShouldBe(1);
        sectionsEl[0].GetProperty("itemIds")[0].GetInt32().ShouldBe(1);

        sectionsEl[1].GetProperty("modeName").GetString().ShouldBe("Manual");
        sectionsEl[1].GetProperty("itemCount").GetInt32().ShouldBe(1);
        sectionsEl[1].GetProperty("itemIds")[0].GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatWorkspace_WithSections_IncludesExcludedItemIds()
    {
        var items = new[] { CreateWorkItem(1, "Task", "Active") };
        var sections = WorkspaceSections.Build(items, excludedIds: new[] { 42, 99 });
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>(), sections: sections);

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("excludedItemIds", out var excluded).ShouldBeTrue();
        excluded.GetArrayLength().ShouldBe(2);
        excluded[0].GetInt32().ShouldBe(42);
        excluded[1].GetInt32().ShouldBe(99);
    }

    [Fact]
    public void FormatWorkspace_WithoutSections_OmitsSectionsProperty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("sections", out _).ShouldBeFalse();
        root.TryGetProperty("excludedItemIds", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkspace_WithTrackedItems_IncludesTrackedArray()
    {
        var items = new[] { CreateWorkItem(1, "Task A", "Active") };
        var tracked = new TrackedItem(1, Domain.Enums.TrackingMode.Single, DateTimeOffset.UtcNow);
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>(),
            trackedItems: new[] { tracked });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("trackedItems", out var trackedArr).ShouldBeTrue();
        trackedArr.GetArrayLength().ShouldBe(1);
        trackedArr[0].GetProperty("workItemId").GetInt32().ShouldBe(1);
        trackedArr[0].GetProperty("mode").GetString().ShouldBe("Single");
    }

    [Fact]
    public void FormatWorkspace_WithExcludedIds_IncludesExcludedArray()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            excludedIds: new[] { 42, 99 });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("excludedIds", out var exclArr).ShouldBeTrue();
        exclArr.GetArrayLength().ShouldBe(2);
        exclArr[0].GetInt32().ShouldBe(42);
        exclArr[1].GetInt32().ShouldBe(99);
    }

    [Fact]
    public void FormatWorkspace_NoTrackedOrExcluded_OmitsBothArrays()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("trackedItems", out _).ShouldBeFalse();
        root.TryGetProperty("excludedIds", out _).ShouldBeFalse();
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

    // ── Relationships in status view ───────────────────────────────

    [Fact]
    public void FormatWorkItem_WithLinks_IncludesLinksArray()
    {
        var item = CreateWorkItem(42, "Linked Item", "Active");
        var links = new List<WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 200, "Successor"),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, links);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.TryGetProperty("links", out var linksElement).ShouldBeTrue();
        linksElement.GetArrayLength().ShouldBe(2);

        var first = linksElement[0];
        first.GetProperty("sourceId").GetInt32().ShouldBe(42);
        first.GetProperty("targetId").GetInt32().ShouldBe(100);
        first.GetProperty("linkType").GetString().ShouldBe("Related");

        var second = linksElement[1];
        second.GetProperty("targetId").GetInt32().ShouldBe(200);
        second.GetProperty("linkType").GetString().ShouldBe("Successor");
    }

    [Fact]
    public void FormatWorkItem_WithNoRelationships_OmitsAllRelationshipFields()
    {
        var item = CreateWorkItem(42, "No Rels", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false, links: null);
        var doc = JsonDocument.Parse(result);

        doc.RootElement.TryGetProperty("links", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("parent", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("children", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkItem_WithParent_IncludesParentObject()
    {
        var item = CreateWorkItem(42, "Child", "Active");
        var parentItem = new WorkItem
        {
            Id = 10, Title = "Parent Epic", State = "Doing",
            Type = WorkItemType.Epic,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, links: null, parent: parentItem);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.TryGetProperty("parent", out var parentEl).ShouldBeTrue();
        parentEl.GetProperty("id").GetInt32().ShouldBe(10);
        parentEl.GetProperty("title").GetString().ShouldBe("Parent Epic");
        parentEl.GetProperty("type").GetString().ShouldBe("Epic");
    }

    [Fact]
    public void FormatWorkItem_WithChildren_IncludesChildrenArray()
    {
        var item = CreateWorkItem(10, "Parent", "Active");
        var children = new List<WorkItem>
        {
            CreateWorkItem(20, "Task A", "Done"),
            CreateWorkItem(21, "Task B", "To Do"),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, links: null, children: children);
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        root.TryGetProperty("children", out var childrenEl).ShouldBeTrue();
        childrenEl.GetArrayLength().ShouldBe(2);
        childrenEl[0].GetProperty("id").GetInt32().ShouldBe(20);
        childrenEl[0].GetProperty("state").GetString().ShouldBe("Done");
        childrenEl[1].GetProperty("id").GetInt32().ShouldBe(21);
    }

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

    // ── FormatQueryResults (Task 3.3) ───────────────────────────────

    [Fact]
    public void FormatQueryResults_ProducesValidJson()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: false, Query: "all items");

        var output = _formatter.FormatQueryResults(result);

        JsonDocument.Parse(output).ShouldNotBeNull();
    }

    [Fact]
    public void FormatQueryResults_HasQueryField()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false, Query: "state = 'Doing'");

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("query").GetString().ShouldBe("state = 'Doing'");
    }

    [Fact]
    public void FormatQueryResults_DefaultQuery_IsAllItems()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("query").GetString().ShouldBe("all items");
    }

    [Fact]
    public void FormatQueryResults_HasCountField()
    {
        var items = new[] { CreateWorkItem(1, "A", "New"), CreateWorkItem(2, "B", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatQueryResults_ZeroItems_CountIsZero()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("count").GetInt32().ShouldBe(0);
        root.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatQueryResults_Truncated_TrueWhenSet()
    {
        var items = new[] { CreateWorkItem(1, "A", "New") };
        var result = new QueryResult(items, IsTruncated: true);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void FormatQueryResults_NotTruncated_FalseWhenUnset()
    {
        var items = new[] { CreateWorkItem(1, "A", "New") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void FormatQueryResults_ItemsHaveRequiredFields()
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
        var result = new QueryResult(new[] { item }, IsTruncated: false, Query: "title contains 'MCP'");

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;
        var itemEl = root.GetProperty("items")[0];

        itemEl.GetProperty("id").GetInt32().ShouldBe(42);
        itemEl.GetProperty("type").GetString().ShouldBe("Issue");
        itemEl.GetProperty("title").GetString().ShouldBe("MCP server integration");
        itemEl.GetProperty("state").GetString().ShouldBe("Doing");
        itemEl.GetProperty("assignedTo").GetString().ShouldBe("Daniel Green");
        itemEl.GetProperty("areaPath").GetString().ShouldBe("Twig");
        itemEl.GetProperty("iterationPath").GetString().ShouldBe("Twig\\Sprint 1");
    }

    [Fact]
    public void FormatQueryResults_NullAssignedTo_WritesNull()
    {
        var item = CreateWorkItem(1, "Unassigned Task", "New");
        var result = new QueryResult(new[] { item }, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("items")[0].GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatQueryResults_MultipleItems_AllPresent()
    {
        var items = new[]
        {
            CreateWorkItem(10, "First", "New"),
            CreateWorkItem(20, "Second", "Active"),
            CreateWorkItem(30, "Third", "Closed"),
        };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);
        var root = JsonDocument.Parse(output).RootElement;

        root.GetProperty("items").GetArrayLength().ShouldBe(3);
        root.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("items")[1].GetProperty("id").GetInt32().ShouldBe(20);
        root.GetProperty("items")[2].GetProperty("id").GetInt32().ShouldBe(30);
    }

    [Fact]
    public void FormatQueryResults_FieldOrder_QueryBeforeCount()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false, Query: "all items");

        var output = _formatter.FormatQueryResults(result);

        // Verify "query" appears before "count" in the output
        var queryIdx = output.IndexOf("\"query\"", StringComparison.Ordinal);
        var countIdx = output.IndexOf("\"count\"", StringComparison.Ordinal);
        queryIdx.ShouldBeLessThan(countIdx);
    }

    // ── Recursive tree output (Task 2073) ───────────────────────────

    [Fact]
    public void FormatTree_WithDescendants_IncludesGrandchildren()
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
        var root = doc.RootElement;

        // Top-level children array should have nested children
        var childElement = root.GetProperty("children")[0];
        childElement.GetProperty("id").GetInt32().ShouldBe(2);
        childElement.GetProperty("children").GetArrayLength().ShouldBe(1);
        childElement.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatTree_WithDescendants_ThreeLevelsDeep()
    {
        var focus = CreateWorkItem(1, "Epic", "Active");
        var child = CreateWorkItemWithParent(2, "Issue", "New", 1);
        var grandchild = CreateWorkItemWithParent(3, "Task", "New", 2);
        var greatGrandchild = CreateWorkItemWithParent(4, "SubTask", "New", 3);

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [2] = new[] { grandchild },
            [3] = new[] { greatGrandchild }
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        var level1 = doc.RootElement.GetProperty("children")[0];
        var level2 = level1.GetProperty("children")[0];
        var level3 = level2.GetProperty("children")[0];
        level3.GetProperty("id").GetInt32().ShouldBe(4);
        level3.GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatTree_ChildWithNoDescendants_HasEmptyChildrenArray()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItemWithParent(2, "Leaf", "New", 1);
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var doc = JsonDocument.Parse(result);

        var childElement = doc.RootElement.GetProperty("children")[0];
        childElement.GetProperty("children").GetArrayLength().ShouldBe(0);
    }
}
