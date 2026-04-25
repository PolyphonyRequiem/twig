using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpResultBuilderTests
{
    // ── ToResult ────────────────────────────────────────────────────

    [Fact]
    public void ToResult_WrapsJsonAsTextContent()
    {
        var json = """{"id":1}""";
        var result = McpResultBuilder.ToResult(json);

        result.IsError.ShouldBeNull();
        result.Content.ShouldNotBeNull();
        result.Content.Count.ShouldBe(1);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldBe(json);
    }

    [Fact]
    public void ToResult_EmptyJson_StillWraps()
    {
        var result = McpResultBuilder.ToResult("");

        result.IsError.ShouldBeNull();
        result.Content.ShouldNotBeNull();
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldBe("");
    }

    // ── ToError ─────────────────────────────────────────────────────

    [Fact]
    public void ToError_SetsIsErrorTrue()
    {
        var result = McpResultBuilder.ToError("Something went wrong");

        result.IsError.ShouldBe(true);
        result.Content.ShouldNotBeNull();
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldBe("Something went wrong");
    }

    [Fact]
    public void ToError_EmptyMessage_StillSetsError()
    {
        var result = McpResultBuilder.ToError("");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldBe("");
    }

    // ── FormatStatus ────────────────────────────────────────────────

    [Fact]
    public void FormatStatus_NoContext_WritesMinimalJson()
    {
        var snapshot = StatusSnapshot.NoContext();

        var result = McpResultBuilder.FormatStatus(snapshot);
        var root = ParseJson(result);

        root.GetProperty("hasContext").GetBoolean().ShouldBeFalse();
        root.GetProperty("item").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("pendingChanges").GetArrayLength().ShouldBe(0);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatStatus_WithItem_IncludesItemAndPendingChanges()
    {
        var item = new WorkItemBuilder(7, "Status Item").AsTask().InState("Done").AssignedTo("Bob").Build();
        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 7,
            Item = item,
            PendingChanges = [new PendingChangeRecord(7, "field", "System.Title", "Old", "New")],
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var root = ParseJson(result);

        root.GetProperty("hasContext").GetBoolean().ShouldBeTrue();
        root.GetProperty("item").GetProperty("id").GetInt32().ShouldBe(7);
        root.GetProperty("pendingChanges").GetArrayLength().ShouldBe(1);

        var change = root.GetProperty("pendingChanges")[0];
        change.GetProperty("workItemId").GetInt32().ShouldBe(7);
        change.GetProperty("changeType").GetString().ShouldBe("field");
        change.GetProperty("fieldName").GetString().ShouldBe("System.Title");
    }

    [Fact]
    public void FormatStatus_MultiplePendingChanges_AllSerialized()
    {
        var item = new WorkItemBuilder(3, "Multi Change").AsTask().InState("Active").Build();
        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 3,
            Item = item,
            PendingChanges =
            [
                new PendingChangeRecord(3, "field", "System.Title", "A", "B"),
                new PendingChangeRecord(3, "state", "System.State", "New", "Active"),
                new PendingChangeRecord(3, "field", "System.AssignedTo", null, "Alice"),
            ],
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var changes = ParseJson(result).GetProperty("pendingChanges");

        changes.GetArrayLength().ShouldBe(3);
        changes[0].GetProperty("oldValue").GetString().ShouldBe("A");
        changes[1].GetProperty("changeType").GetString().ShouldBe("state");
        changes[2].GetProperty("oldValue").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatStatus_Unreachable_IncludesErrorFields()
    {
        var snapshot = StatusSnapshot.Unreachable(99, 99, "Not found");

        var result = McpResultBuilder.FormatStatus(snapshot);
        var root = ParseJson(result);

        root.GetProperty("unreachableId").GetInt32().ShouldBe(99);
        root.GetProperty("unreachableReason").GetString().ShouldBe("Not found");
    }

    [Fact]
    public void FormatStatus_NoUnreachable_OmitsErrorFields()
    {
        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 1,
            Item = WorkItemBuilder.Simple(1, "OK"),
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var root = ParseJson(result);

        root.TryGetProperty("unreachableId", out _).ShouldBeFalse();
        root.TryGetProperty("unreachableReason", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatStatus_WithSeeds_IncludesSeedArray()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().Build();
        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 1,
            Item = new WorkItemBuilder(1, "Active").AsEpic().InState("Active").Build(),
            Seeds = [seed],
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var root = ParseJson(result);

        root.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        root.GetProperty("seeds")[0].GetProperty("isSeed").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void FormatStatus_ItemIncludesAreaAndIterationPaths()
    {
        var item = new WorkItemBuilder(1, "Pathed")
            .AsTask()
            .WithAreaPath(@"Project\Team")
            .WithIterationPath(@"Project\Sprint 1")
            .Build();

        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 1,
            Item = item,
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var itemJson = ParseJson(result).GetProperty("item");

        itemJson.TryGetProperty("areaPath", out _).ShouldBeTrue();
        itemJson.TryGetProperty("iterationPath", out _).ShouldBeTrue();
    }

    // ── FormatTree ──────────────────────────────────────────────────

    [Fact]
    public void FormatTree_ProducesValidStructure()
    {
        var focus = new WorkItemBuilder(10, "Focus").AsEpic().InState("Active").AssignedTo("Alice").Build();
        var parent = new WorkItemBuilder(5, "Parent").AsFeature().InState("Active").AssignedTo("Bob").Build();
        var child1 = new WorkItemBuilder(20, "Child 1").AsTask().Build();
        var child2 = new WorkItemBuilder(21, "Child 2").AsTask().InState("Active").AssignedTo("Carol").Build();
        var link = new WorkItemLink(10, 30, "Related");

        var tree = WorkTree.Build(focus, [parent], [child1, child2], focusedItemLinks: [link]);

        var result = McpResultBuilder.FormatTree(tree, 2);
        var root = ParseJson(result);

        root.GetProperty("focus").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(1);
        root.GetProperty("parentChain")[0].GetProperty("id").GetInt32().ShouldBe(5);
        root.GetProperty("children").GetArrayLength().ShouldBe(2);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(2);
        root.GetProperty("links").GetArrayLength().ShouldBe(1);
        root.GetProperty("links")[0].GetProperty("linkType").GetString().ShouldBe("Related");
    }

    [Fact]
    public void FormatTree_EmptyChildren_WritesEmptyArrays()
    {
        var focus = new WorkItemBuilder(1, "Solo").AsBug().InState("Active").Build();
        var tree = WorkTree.Build(focus, [], []);

        var result = McpResultBuilder.FormatTree(tree, 0);
        var root = ParseJson(result);

        root.GetProperty("children").GetArrayLength().ShouldBe(0);
        root.GetProperty("parentChain").GetArrayLength().ShouldBe(0);
        root.GetProperty("links").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatTree_MultipleLinks_IncludesSourceAndTargetIds()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var links = new[]
        {
            new WorkItemLink(10, 20, "Related"),
            new WorkItemLink(10, 30, "Predecessor"),
        };
        var tree = WorkTree.Build(focus, [], [], focusedItemLinks: links);

        var result = McpResultBuilder.FormatTree(tree, 0);
        var linksArr = ParseJson(result).GetProperty("links");

        linksArr.GetArrayLength().ShouldBe(2);
        linksArr[0].GetProperty("sourceId").GetInt32().ShouldBe(10);
        linksArr[0].GetProperty("targetId").GetInt32().ShouldBe(20);
        linksArr[1].GetProperty("sourceId").GetInt32().ShouldBe(10);
        linksArr[1].GetProperty("targetId").GetInt32().ShouldBe(30);
        linksArr[1].GetProperty("linkType").GetString().ShouldBe("Predecessor");
    }

    [Fact]
    public void FormatTree_DeepParentChain_PreservesOrder()
    {
        var focus = WorkItemBuilder.Simple(100, "Focus");
        var grandparent = new WorkItemBuilder(1, "Grandparent").AsEpic().Build();
        var parent = new WorkItemBuilder(50, "Parent").AsFeature().Build();
        var tree = WorkTree.Build(focus, [grandparent, parent], []);

        var result = McpResultBuilder.FormatTree(tree, 0);
        var chain = ParseJson(result).GetProperty("parentChain");

        chain.GetArrayLength().ShouldBe(2);
        chain[0].GetProperty("id").GetInt32().ShouldBe(1);
        chain[1].GetProperty("id").GetInt32().ShouldBe(50);
    }

    [Fact]
    public void FormatTree_TotalChildren_ReflectsActualCount_NotTruncated()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child1 = new WorkItemBuilder(20, "Child 1").AsTask().Build();
        var child2 = new WorkItemBuilder(21, "Child 2").AsTask().Build();
        // Tree has 2 children but totalChildren is 5 (e.g., depth-truncated)
        var tree = WorkTree.Build(focus, [], [child1, child2]);

        var result = McpResultBuilder.FormatTree(tree, 5);
        var root = ParseJson(result);

        root.GetProperty("children").GetArrayLength().ShouldBe(2);
        root.GetProperty("totalChildren").GetInt32().ShouldBe(5);
    }

    [Fact]
    public void FormatTree_SiblingCounts_SerializedAsIdToCountMap()
    {
        var focus = new WorkItemBuilder(10, "Focus").WithParent(5).Build();
        var parent = new WorkItemBuilder(5, "Parent").AsFeature().Build();
        var siblingCounts = new Dictionary<int, int?> { [5] = null, [10] = 3 };
        var tree = WorkTree.Build(focus, [parent], [], siblingCounts);

        var result = McpResultBuilder.FormatTree(tree, 0);
        var counts = ParseJson(result).GetProperty("siblingCounts");

        counts.GetProperty("5").ValueKind.ShouldBe(JsonValueKind.Null);
        counts.GetProperty("10").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatTree_NoSiblingCounts_OmitsSiblingCountsKey()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, [], []);

        var result = McpResultBuilder.FormatTree(tree, 0);
        ParseJson(result).TryGetProperty("siblingCounts", out _).ShouldBeFalse();
    }

    // ── FormatWorkspace ─────────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_ProducesValidStructure()
    {
        var context = new WorkItemBuilder(1, "Context").AsEpic().InState("Active").AssignedTo("Alice").Build();
        var sprint = new WorkItemBuilder(2, "Sprint Item").AsTask().InState("Active").AssignedTo("Bob").Build();
        var seed = new WorkItemBuilder(-1, "Seed").AsBug().AsSeed().Build();
        var workspace = Workspace.Build(context, [sprint], [seed]);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        var root = ParseJson(result);

        root.GetProperty("context").GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(1);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        root.GetProperty("staleSeeds").ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatWorkspace_StaleSeed_AppearsInStaleArray()
    {
        var staleSeed = new WorkItemBuilder(-1, "Old Seed").AsTask().AsSeed(daysOld: 30).Build();
        var freshSeed = new WorkItemBuilder(-2, "New Seed").AsTask().AsSeed(daysOld: 1).Build();
        var workspace = Workspace.Build(null, [], [staleSeed, freshSeed]);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        var staleIds = ParseJson(result).GetProperty("staleSeeds");

        staleIds.GetArrayLength().ShouldBe(1);
        staleIds[0].GetInt32().ShouldBe(-1);
    }

    [Fact]
    public void FormatWorkspace_DirtyItems_ReflectedInDirtyCount()
    {
        var dirty1 = new WorkItemBuilder(1, "Dirty 1").AsTask().Dirty().Build();
        var dirty2 = new WorkItemBuilder(2, "Dirty 2").AsTask().Dirty().Build();
        var clean = new WorkItemBuilder(3, "Clean").AsTask().Build();
        var workspace = Workspace.Build(null, [dirty1, dirty2, clean], []);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        ParseJson(result).GetProperty("dirtyCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatWorkspace_EmptyWorkspace_WritesNullContextAndZeroCounts()
    {
        var workspace = Workspace.Build(null, [], []);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        var root = ParseJson(result);

        root.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("sprintItems").GetArrayLength().ShouldBe(0);
        root.GetProperty("seeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("staleSeeds").GetArrayLength().ShouldBe(0);
        root.GetProperty("dirtyCount").GetInt32().ShouldBe(0);
    }

    // ── FormatFlushSummary ──────────────────────────────────────────

    [Fact]
    public void FormatFlushSummary_ProducesValidJsonWithAllFields()
    {
        var summary = new McpFlushSummary
        {
            Flushed = 3,
            Failed = 1,
            Failures = [new McpFlushItemFailure { WorkItemId = 42, Reason = "Conflict" }],
        };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        var root = ParseJson(result);

        root.GetProperty("flushed").GetInt32().ShouldBe(3);
        root.GetProperty("failed").GetInt32().ShouldBe(1);
        root.GetProperty("failures").GetArrayLength().ShouldBe(1);
        root.GetProperty("failures")[0].GetProperty("workItemId").GetInt32().ShouldBe(42);
        root.GetProperty("failures")[0].GetProperty("reason").GetString().ShouldBe("Conflict");
    }

    [Fact]
    public void FormatFlushSummary_ZeroFailures_WritesEmptyArray()
    {
        var summary = new McpFlushSummary { Flushed = 5, Failed = 0 };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        ParseJson(result).GetProperty("failures").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatFlushSummary_MultipleFailures_AllSerialized()
    {
        var summary = new McpFlushSummary
        {
            Flushed = 1,
            Failed = 3,
            Failures =
            [
                new McpFlushItemFailure { WorkItemId = 10, Reason = "Conflict" },
                new McpFlushItemFailure { WorkItemId = 20, Reason = "Not found" },
                new McpFlushItemFailure { WorkItemId = 30, Reason = "Unauthorized" },
            ],
        };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        var failures = ParseJson(result).GetProperty("failures");

        failures.GetArrayLength().ShouldBe(3);
        failures[0].GetProperty("workItemId").GetInt32().ShouldBe(10);
        failures[1].GetProperty("reason").GetString().ShouldBe("Not found");
        failures[2].GetProperty("workItemId").GetInt32().ShouldBe(30);
    }

    [Fact]
    public void FormatFlushSummary_UsesCamelCaseNaming()
    {
        var summary = new McpFlushSummary { Flushed = 1 };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        var root = ParseJson(result);

        root.TryGetProperty("flushed", out _).ShouldBeTrue();
        root.TryGetProperty("failed", out _).ShouldBeTrue();
        root.TryGetProperty("failures", out _).ShouldBeTrue();

        root.TryGetProperty("Flushed", out _).ShouldBeFalse();
        root.TryGetProperty("Failed", out _).ShouldBeFalse();
        root.TryGetProperty("Failures", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatFlushSummary_ResultIsNotError()
    {
        var summary = new McpFlushSummary { Flushed = 1 };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        result.IsError.ShouldBeNull();
    }

    // ── FormatDiscardNone ──────────────────────────────────────────

    [Fact]
    public void FormatDiscardNone_ProducesExpectedFields()
    {
        var result = McpResultBuilder.FormatDiscardNone(42, "My work item");
        var root = ParseJson(result);

        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("My work item");
        root.GetProperty("discarded").GetBoolean().ShouldBe(false);
        root.GetProperty("message").GetString()!.ShouldContain("No pending changes");
    }

    [Fact]
    public void FormatDiscardNone_TitleWithControlCharacters_ProducesValidJson()
    {
        var result = McpResultBuilder.FormatDiscardNone(99, "My\nTask\twith\rcontrols");
        var root = ParseJson(result);

        root.GetProperty("discarded").GetBoolean().ShouldBe(false);
        root.GetProperty("id").GetInt32().ShouldBe(99);
        root.GetProperty("title").GetString()!.ShouldContain("My");
    }

    // ── FormatDiscard ───────────────────────────────────────────────

    [Fact]
    public void FormatDiscard_ProducesExpectedFields()
    {
        var result = McpResultBuilder.FormatDiscard(7, "Fix bug", notes: 2, fieldEdits: 1);
        var root = ParseJson(result);

        root.GetProperty("id").GetInt32().ShouldBe(7);
        root.GetProperty("title").GetString().ShouldBe("Fix bug");
        root.GetProperty("discarded").GetBoolean().ShouldBe(true);
        root.GetProperty("notesDiscarded").GetInt32().ShouldBe(2);
        root.GetProperty("fieldEditsDiscarded").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void FormatDiscard_TitleWithControlCharacters_ProducesValidJson()
    {
        var result = McpResultBuilder.FormatDiscard(50, "Line1\nLine2\t\r", notes: 0, fieldEdits: 3);
        var root = ParseJson(result);

        root.GetProperty("discarded").GetBoolean().ShouldBe(true);
        root.GetProperty("id").GetInt32().ShouldBe(50);
        root.GetProperty("title").GetString()!.ShouldContain("Line1");
        root.GetProperty("fieldEditsDiscarded").GetInt32().ShouldBe(3);
    }

    // ── FormatWorkItem ─────────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ProducesFullWorkItemJson()
    {
        var item = new WorkItemBuilder(42, "My Feature")
            .AsFeature()
            .InState("Active")
            .AssignedTo("Alice")
            .WithAreaPath(@"Project\TeamA")
            .WithIterationPath(@"Project\Sprint 1")
            .WithParent(10)
            .Build();

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("My Feature");
        root.GetProperty("type").GetString().ShouldBe("Feature");
        root.GetProperty("state").GetString().ShouldBe("Active");
        root.GetProperty("assignedTo").GetString().ShouldBe("Alice");
        root.GetProperty("areaPath").GetString().ShouldBe(@"Project\TeamA");
        root.GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 1");
        root.GetProperty("parentId").GetInt32().ShouldBe(10);
        root.GetProperty("isDirty").GetBoolean().ShouldBeFalse();
        root.GetProperty("isSeed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkItem_WithFields_IncludesFieldsObject()
    {
        var item = new WorkItemBuilder(1, "With Fields")
            .AsTask()
            .WithField("System.Description", "Some description")
            .WithField("Custom.Priority", "High")
            .Build();

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.TryGetProperty("fields", out var fields).ShouldBeTrue();
        fields.GetProperty("System.Description").GetString().ShouldBe("Some description");
        fields.GetProperty("Custom.Priority").GetString().ShouldBe("High");
    }

    [Fact]
    public void FormatWorkItem_NoFields_OmitsFieldsObject()
    {
        var item = WorkItemBuilder.Simple(1, "No Fields");

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.TryGetProperty("fields", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatWorkItem_NullParentId_WritesNull()
    {
        var item = WorkItemBuilder.Simple(5, "Root Item");

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkItem_WithWorkspace_IncludesWorkspaceKey()
    {
        var item = WorkItemBuilder.Simple(1, "Item");

        var result = McpResultBuilder.FormatWorkItem(item, workspace: "myorg/myproject");
        var root = ParseJson(result);

        root.GetProperty("workspace").GetString().ShouldBe("myorg/myproject");
    }

    [Fact]
    public void FormatWorkItem_NullWorkspace_WritesNull()
    {
        var item = WorkItemBuilder.Simple(1, "Item");

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.GetProperty("workspace").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkItem_NullFieldValue_WritesNullInFields()
    {
        var item = new WorkItemBuilder(1, "Null Field")
            .AsTask()
            .WithField("System.Description", null)
            .Build();

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.GetProperty("fields").GetProperty("System.Description").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkItem_SeedItem_ReflectsIsSeed()
    {
        var item = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().Build();

        var result = McpResultBuilder.FormatWorkItem(item);
        var root = ParseJson(result);

        root.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
    }

    // ── FormatQueryResults ──────────────────────────────────────────

    [Fact]
    public void FormatQueryResults_HappyPath_ProducesExpectedStructure()
    {
        var items = new List<WorkItem>
        {
            new WorkItemBuilder(1, "Bug 1").AsBug().InState("Active").Build(),
            new WorkItemBuilder(2, "Bug 2").AsBug().InState("Resolved").Build(),
        };

        var result = McpResultBuilder.FormatQueryResults(items, isTruncated: false, "type=Bug");
        var root = ParseJson(result);

        root.GetProperty("items").GetArrayLength().ShouldBe(2);
        root.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("items")[1].GetProperty("id").GetInt32().ShouldBe(2);
        root.GetProperty("totalCount").GetInt32().ShouldBe(2);
        root.GetProperty("isTruncated").GetBoolean().ShouldBeFalse();
        root.GetProperty("queryDescription").GetString().ShouldBe("type=Bug");
    }

    [Fact]
    public void FormatQueryResults_Truncated_SetsFlag()
    {
        var items = new List<WorkItem>
        {
            WorkItemBuilder.Simple(1, "Item 1"),
        };

        var result = McpResultBuilder.FormatQueryResults(items, isTruncated: true, "state=Active");
        var root = ParseJson(result);

        root.GetProperty("isTruncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void FormatQueryResults_EmptyResults_WritesEmptyArray()
    {
        var result = McpResultBuilder.FormatQueryResults([], isTruncated: false, "type=Epic");
        var root = ParseJson(result);

        root.GetProperty("items").GetArrayLength().ShouldBe(0);
        root.GetProperty("totalCount").GetInt32().ShouldBe(0);
        root.GetProperty("isTruncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void FormatQueryResults_WithWorkspace_IncludesWorkspace()
    {
        var result = McpResultBuilder.FormatQueryResults([], isTruncated: false, "query", workspace: "org/proj");
        var root = ParseJson(result);

        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    // ── FormatChildren ──────────────────────────────────────────────

    [Fact]
    public void FormatChildren_ProducesExpectedStructure()
    {
        var children = new List<WorkItem>
        {
            new WorkItemBuilder(20, "Child 1").AsTask().InState("Active").Build(),
            new WorkItemBuilder(21, "Child 2").AsTask().InState("New").Build(),
        };

        var result = McpResultBuilder.FormatChildren(10, children);
        var root = ParseJson(result);

        root.GetProperty("parentId").GetInt32().ShouldBe(10);
        root.GetProperty("children").GetArrayLength().ShouldBe(2);
        root.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(20);
        root.GetProperty("children")[1].GetProperty("id").GetInt32().ShouldBe(21);
        root.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatChildren_NoChildren_WritesEmptyArray()
    {
        var result = McpResultBuilder.FormatChildren(5, []);
        var root = ParseJson(result);

        root.GetProperty("parentId").GetInt32().ShouldBe(5);
        root.GetProperty("children").GetArrayLength().ShouldBe(0);
        root.GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatChildren_WithWorkspace_IncludesWorkspace()
    {
        var result = McpResultBuilder.FormatChildren(1, [], workspace: "org/proj");
        var root = ParseJson(result);

        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    // ── FormatParent ────────────────────────────────────────────────

    [Fact]
    public void FormatParent_WithParent_ProducesExpectedStructure()
    {
        var child = new WorkItemBuilder(20, "Child Task").AsTask().InState("Active").WithParent(10).Build();
        var parent = new WorkItemBuilder(10, "Parent Feature")
            .AsFeature()
            .InState("Active")
            .AssignedTo("Bob")
            .WithAreaPath(@"Project\Team")
            .WithIterationPath(@"Project\Sprint 1")
            .Build();

        var result = McpResultBuilder.FormatParent(child, parent);
        var root = ParseJson(result);

        root.GetProperty("child").GetProperty("id").GetInt32().ShouldBe(20);
        root.GetProperty("child").GetProperty("title").GetString().ShouldBe("Child Task");

        root.GetProperty("parent").GetProperty("id").GetInt32().ShouldBe(10);
        root.GetProperty("parent").GetProperty("title").GetString().ShouldBe("Parent Feature");
        root.GetProperty("parent").GetProperty("areaPath").GetString().ShouldBe(@"Project\Team");
        root.GetProperty("parent").GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 1");
    }

    [Fact]
    public void FormatParent_NullParent_WritesNull()
    {
        var child = new WorkItemBuilder(5, "Root Item").AsEpic().InState("New").Build();

        var result = McpResultBuilder.FormatParent(child, null);
        var root = ParseJson(result);

        root.GetProperty("child").GetProperty("id").GetInt32().ShouldBe(5);
        root.GetProperty("parent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatParent_WithWorkspace_IncludesWorkspace()
    {
        var child = WorkItemBuilder.Simple(1, "Child");

        var result = McpResultBuilder.FormatParent(child, null, workspace: "org/proj");
        var root = ParseJson(result);

        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    // ── FormatSprint ────────────────────────────────────────────────

    [Fact]
    public void FormatSprint_WithItems_ProducesExpectedStructure()
    {
        var iteration = IterationPath.Parse(@"Project\Sprint 5").Value;
        var items = new List<WorkItem>
        {
            new WorkItemBuilder(1, "Task 1").AsTask().InState("Active").Build(),
            new WorkItemBuilder(2, "Task 2").AsTask().InState("New").Build(),
        };

        var result = McpResultBuilder.FormatSprint(iteration, items);
        var root = ParseJson(result);

        root.GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 5");
        root.GetProperty("items").GetArrayLength().ShouldBe(2);
        root.GetProperty("items")[0].GetProperty("id").GetInt32().ShouldBe(1);
        root.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatSprint_NullItems_WritesNullAndOmitsCount()
    {
        var iteration = IterationPath.Parse(@"Project\Sprint 1").Value;

        var result = McpResultBuilder.FormatSprint(iteration, null);
        var root = ParseJson(result);

        root.GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 1");
        root.GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Null);
        root.TryGetProperty("count", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatSprint_EmptyItems_WritesEmptyArrayAndZeroCount()
    {
        var iteration = IterationPath.Parse(@"Project\Sprint 2").Value;

        var result = McpResultBuilder.FormatSprint(iteration, []);
        var root = ParseJson(result);

        root.GetProperty("items").GetArrayLength().ShouldBe(0);
        root.GetProperty("count").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void FormatSprint_WithWorkspace_IncludesWorkspace()
    {
        var iteration = IterationPath.Parse(@"Project\Sprint 1").Value;

        var result = McpResultBuilder.FormatSprint(iteration, null, workspace: "org/proj");
        var root = ParseJson(result);

        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    // ── FormatCreated ────────────────────────────────────────────────

    [Fact]
    public void FormatCreated_WritesExpectedFields()
    {
        var item = new WorkItemBuilder(42, "New Feature")
            .AsTask()
            .InState("New")
            .WithAreaPath(@"Project\Team")
            .WithIterationPath(@"Project\Sprint 1")
            .WithParent(10)
            .Build();

        var result = McpResultBuilder.FormatCreated(item, "https://dev.azure.com/org/proj/_workitems/edit/42", workspace: "org/proj");
        var root = ParseJson(result);

        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("New Feature");
        root.GetProperty("type").GetString().ShouldBe("Task");
        root.GetProperty("state").GetString().ShouldBe("New");
        root.GetProperty("areaPath").GetString().ShouldBe(@"Project\Team");
        root.GetProperty("iterationPath").GetString().ShouldBe(@"Project\Sprint 1");
        root.GetProperty("url").GetString().ShouldBe("https://dev.azure.com/org/proj/_workitems/edit/42");
        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    [Fact]
    public void FormatCreated_NullWorkspace_WritesJsonNull()
    {
        var item = new WorkItemBuilder(99, "Solo Item").AsBug().InState("New").Build();

        var result = McpResultBuilder.FormatCreated(item, "https://dev.azure.com/org/proj/_workitems/edit/99");
        var root = ParseJson(result);

        root.GetProperty("workspace").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── FormatLinked ────────────────────────────────────────────────

    [Fact]
    public void FormatLinked_WritesExpectedFields()
    {
        var result = McpResultBuilder.FormatLinked(42, 99, "parent");
        var root = ParseJson(result);

        root.GetProperty("sourceId").GetInt32().ShouldBe(42);
        root.GetProperty("targetId").GetInt32().ShouldBe(99);
        root.GetProperty("linkType").GetString().ShouldBe("parent");
        root.GetProperty("linked").GetBoolean().ShouldBeTrue();
        root.TryGetProperty("warning", out _).ShouldBeFalse();
    }

    [Fact]
    public void FormatLinked_WithWarning_WritesWarningField()
    {
        var result = McpResultBuilder.FormatLinked(1, 2, "related", warning: "Cache sync failed");
        ParseJson(result).GetProperty("warning").GetString().ShouldBe("Cache sync failed");
    }

    // ── FormatTree — Recursive Children ─────────────────────────────

    [Fact]
    public void FormatTree_WithDescendants_WritesRecursiveChildrenArrays()
    {
        var focus = WorkItemBuilder.Simple(1, "Focus");
        var child = new WorkItemBuilder(10, "Child").AsTask().WithParent(1).Build();
        var grandchild = new WorkItemBuilder(100, "Grandchild").AsTask().WithParent(10).Build();

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { grandchild }
        };

        var tree = WorkTree.Build(focus, [], [child], descendantsByParentId: descendants);
        var result = McpResultBuilder.FormatTree(tree, 1);
        var root = ParseJson(result);

        var children = root.GetProperty("children");
        children.GetArrayLength().ShouldBe(1);

        var childNode = children[0];
        childNode.GetProperty("id").GetInt32().ShouldBe(10);
        childNode.GetProperty("children").GetArrayLength().ShouldBe(1);
        childNode.GetProperty("children")[0].GetProperty("id").GetInt32().ShouldBe(100);
    }

    [Fact]
    public void FormatTree_ThreeLevelNesting_WritesAllLevels()
    {
        var focus = WorkItemBuilder.Simple(1, "Epic");
        var child = new WorkItemBuilder(10, "Issue").AsTask().WithParent(1).Build();
        var grandchild = new WorkItemBuilder(100, "Task").AsTask().WithParent(10).Build();
        var greatGrandchild = new WorkItemBuilder(1000, "Subtask").AsTask().WithParent(100).Build();

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { grandchild },
            [100] = new[] { greatGrandchild }
        };

        var tree = WorkTree.Build(focus, [], [child], descendantsByParentId: descendants);
        var result = McpResultBuilder.FormatTree(tree, 1);
        var root = ParseJson(result);

        var level1 = root.GetProperty("children")[0];
        level1.GetProperty("id").GetInt32().ShouldBe(10);

        var level2 = level1.GetProperty("children")[0];
        level2.GetProperty("id").GetInt32().ShouldBe(100);

        var level3 = level2.GetProperty("children")[0];
        level3.GetProperty("id").GetInt32().ShouldBe(1000);
        level3.GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatTree_NoDescendants_ChildrenHaveEmptyChildrenArrays()
    {
        var focus = WorkItemBuilder.Simple(1, "Focus");
        var child1 = new WorkItemBuilder(10, "Child 1").AsTask().Build();
        var child2 = new WorkItemBuilder(11, "Child 2").AsTask().Build();

        var tree = WorkTree.Build(focus, [], [child1, child2]);
        var result = McpResultBuilder.FormatTree(tree, 2);
        var root = ParseJson(result);

        var children = root.GetProperty("children");
        children.GetArrayLength().ShouldBe(2);
        children[0].GetProperty("children").GetArrayLength().ShouldBe(0);
        children[1].GetProperty("children").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FormatTree_ChildNodeIncludesCoreFields()
    {
        var focus = WorkItemBuilder.Simple(1, "Focus");
        var child = new WorkItemBuilder(10, "Child Item")
            .AsTask().InState("Active").AssignedTo("Alice").WithParent(1).Build();

        var tree = WorkTree.Build(focus, [], [child]);
        var result = McpResultBuilder.FormatTree(tree, 1);
        var childNode = ParseJson(result).GetProperty("children")[0];

        childNode.GetProperty("id").GetInt32().ShouldBe(10);
        childNode.GetProperty("title").GetString().ShouldBe("Child Item");
        childNode.GetProperty("state").GetString().ShouldBe("Active");
        childNode.GetProperty("assignedTo").GetString().ShouldBe("Alice");
        childNode.GetProperty("parentId").GetInt32().ShouldBe(1);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
