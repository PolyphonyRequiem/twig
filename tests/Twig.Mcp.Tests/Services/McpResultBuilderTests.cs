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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var changes = doc.RootElement.GetProperty("pendingChanges");

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        using var doc = JsonDocument.Parse(GetJsonText(result));

        doc.RootElement.TryGetProperty("unreachableId", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("unreachableReason", out _).ShouldBeFalse();
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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        doc.RootElement.GetProperty("seeds")[0].GetProperty("isSeed").GetBoolean().ShouldBeTrue();
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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var itemJson = doc.RootElement.GetProperty("item");

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("children").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("parentChain").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("links").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("totalChildren").GetInt32().ShouldBe(0);
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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var linksArr = doc.RootElement.GetProperty("links");

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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var chain = doc.RootElement.GetProperty("parentChain");

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
        using var doc = JsonDocument.Parse(GetJsonText(result));

        doc.RootElement.GetProperty("children").GetArrayLength().ShouldBe(2);
        doc.RootElement.GetProperty("totalChildren").GetInt32().ShouldBe(5);
    }

    [Fact]
    public void FormatTree_SiblingCounts_SerializedAsIdToCountMap()
    {
        var focus = new WorkItemBuilder(10, "Focus").WithParent(5).Build();
        var parent = new WorkItemBuilder(5, "Parent").AsFeature().Build();
        var siblingCounts = new Dictionary<int, int?> { [5] = null, [10] = 3 };
        var tree = WorkTree.Build(focus, [parent], [], siblingCounts);

        var result = McpResultBuilder.FormatTree(tree, 0);
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var counts = doc.RootElement.GetProperty("siblingCounts");

        counts.GetProperty("5").ValueKind.ShouldBe(JsonValueKind.Null);
        counts.GetProperty("10").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void FormatTree_NoSiblingCounts_OmitsSiblingCountsKey()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, [], []);

        var result = McpResultBuilder.FormatTree(tree, 0);
        using var doc = JsonDocument.Parse(GetJsonText(result));

        doc.RootElement.TryGetProperty("siblingCounts", out _).ShouldBeFalse();
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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var staleIds = doc.RootElement.GetProperty("staleSeeds");

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
        using var doc = JsonDocument.Parse(GetJsonText(result));

        doc.RootElement.GetProperty("dirtyCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void FormatWorkspace_EmptyWorkspace_WritesNullContextAndZeroCounts()
    {
        var workspace = Workspace.Build(null, [], []);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var root = doc.RootElement;

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("failures").GetArrayLength().ShouldBe(0);
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
        using var doc = JsonDocument.Parse(GetJsonText(result));
        var failures = doc.RootElement.GetProperty("failures");

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
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

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

    // ── Helpers ─────────────────────────────────────────────────────

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = GetJsonText(result);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string GetJsonText(CallToolResult result)
    {
        return result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
    }
}
