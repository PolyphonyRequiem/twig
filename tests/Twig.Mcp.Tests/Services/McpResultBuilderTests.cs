using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;
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

    // ── FormatWorkItem ──────────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ProducesValidJsonWithAllFields()
    {
        var item = CreateWorkItem(42, "Test Item", "Task", "Active", "Alice", parentId: 10);

        var result = McpResultBuilder.FormatWorkItem(item);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("title").GetString().ShouldBe("Test Item");
        root.GetProperty("type").GetString().ShouldBe("Task");
        root.GetProperty("state").GetString().ShouldBe("Active");
        root.GetProperty("assignedTo").GetString().ShouldBe("Alice");
        root.GetProperty("isDirty").GetBoolean().ShouldBeFalse();
        root.GetProperty("isSeed").GetBoolean().ShouldBeFalse();
        root.GetProperty("parentId").GetInt32().ShouldBe(10);
        root.TryGetProperty("areaPath", out _).ShouldBeTrue();
        root.TryGetProperty("iterationPath", out _).ShouldBeTrue();
    }

    [Fact]
    public void FormatWorkItem_NullParentId_WritesNull()
    {
        var item = CreateWorkItem(1, "Root", "Epic", "New", null, parentId: null);

        var result = McpResultBuilder.FormatWorkItem(item);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
        doc.RootElement.GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void FormatWorkItem_ResultIsNotError()
    {
        var item = CreateWorkItem(1, "Item", "Bug", "Active", null);

        var result = McpResultBuilder.FormatWorkItem(item);
        result.IsError.ShouldBeNull();
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
        var item = CreateWorkItem(7, "Status Item", "Task", "Done", "Bob");
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
    public void FormatStatus_WithSeeds_IncludesSeedArray()
    {
        var seed = CreateSeedWorkItem(-1, "My Seed", "Task");
        var snapshot = new StatusSnapshot
        {
            HasContext = true,
            ActiveId = 1,
            Item = CreateWorkItem(1, "Active", "Epic", "Active", null),
            Seeds = [seed],
        };

        var result = McpResultBuilder.FormatStatus(snapshot);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("seeds").GetArrayLength().ShouldBe(1);
        doc.RootElement.GetProperty("seeds")[0].GetProperty("isSeed").GetBoolean().ShouldBeTrue();
    }

    // ── FormatTree ──────────────────────────────────────────────────

    [Fact]
    public void FormatTree_ProducesValidStructure()
    {
        var focus = CreateWorkItem(10, "Focus", "Epic", "Active", "Alice");
        var parent = CreateWorkItem(5, "Parent", "Feature", "Active", "Bob");
        var child1 = CreateWorkItem(20, "Child 1", "Task", "New", null);
        var child2 = CreateWorkItem(21, "Child 2", "Task", "Active", "Carol");
        var link = new WorkItemLink(10, 30, "Related");

        var tree = WorkTree.Build(focus, [parent], [child1, child2], focusedItemLinks: [link]);

        var result = McpResultBuilder.FormatTree(tree);
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
        var focus = CreateWorkItem(1, "Solo", "Bug", "Active", null);
        var tree = WorkTree.Build(focus, [], []);

        var result = McpResultBuilder.FormatTree(tree);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("children").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("parentChain").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("links").GetArrayLength().ShouldBe(0);
        doc.RootElement.GetProperty("totalChildren").GetInt32().ShouldBe(0);
    }

    // ── FormatWorkspace ─────────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_ProducesValidStructure()
    {
        var context = CreateWorkItem(1, "Context", "Epic", "Active", "Alice");
        var sprint = CreateWorkItem(2, "Sprint Item", "Task", "Active", "Bob");
        var seed = CreateSeedWorkItem(-1, "Seed", "Bug");
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
    public void FormatWorkspace_NullContext_WritesNull()
    {
        var workspace = Workspace.Build(null, [], []);

        var result = McpResultBuilder.FormatWorkspace(workspace, staleDays: 7);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("context").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── FormatFlushSummary ──────────────────────────────────────────

    [Fact]
    public void FormatFlushSummary_SerializesViaMcpJsonContext()
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
    public void FormatFlushSummary_UsesCamelCaseNaming()
    {
        var summary = new McpFlushSummary { Flushed = 1 };

        var result = McpResultBuilder.FormatFlushSummary(summary);
        var json = GetJsonText(result);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify camelCase: properties start with lowercase
        root.TryGetProperty("flushed", out _).ShouldBeTrue();
        root.TryGetProperty("failed", out _).ShouldBeTrue();
        root.TryGetProperty("failures", out _).ShouldBeTrue();

        // Verify PascalCase variants are absent
        root.TryGetProperty("Flushed", out _).ShouldBeFalse();
        root.TryGetProperty("Failed", out _).ShouldBeFalse();
        root.TryGetProperty("Failures", out _).ShouldBeFalse();
    }

    // ── McpJsonContext source-gen ────────────────────────────────────

    [Fact]
    public void McpJsonContext_RoundTripFlushSummary()
    {
        var original = new McpFlushSummary
        {
            Flushed = 10,
            Failed = 2,
            Failures =
            [
                new McpFlushItemFailure { WorkItemId = 1, Reason = "Conflict" },
                new McpFlushItemFailure { WorkItemId = 2, Reason = "Not found" },
            ],
        };

        var json = JsonSerializer.Serialize(original, McpJsonContext.Default.McpFlushSummary);
        var deserialized = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpFlushSummary);

        deserialized.ShouldNotBeNull();
        deserialized.Flushed.ShouldBe(10);
        deserialized.Failed.ShouldBe(2);
        deserialized.Failures.Count.ShouldBe(2);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static string GetJsonText(CallToolResult result)
    {
        return result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
    }

    private static WorkItem CreateWorkItem(
        int id, string title, string typeName, string state, string? assignedTo, int? parentId = null)
    {
        var typeResult = WorkItemType.Parse(typeName);
        return new WorkItem
        {
            Id = id,
            Title = title,
            Type = typeResult.Value,
            State = state,
            AssignedTo = assignedTo,
            ParentId = parentId,
        };
    }

    private static WorkItem CreateSeedWorkItem(int id, string title, string typeName)
    {
        var typeResult = WorkItemType.Parse(typeName);
        return new WorkItem
        {
            Id = id,
            Title = title,
            Type = typeResult.Value,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
