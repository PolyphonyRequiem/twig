using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Domain.ReadModels;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="McpResultBuilder.FormatVerification"/>.
/// Validates JSON shape, verified/unverified paths, incomplete item serialization,
/// incompleteCount consistency, and nullable field handling.
/// </summary>
public sealed class McpResultBuilderVerificationTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Verified = true — all JSON shape fields present, empty incomplete
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatVerification_AllVerified_WritesCorrectJsonShape()
    {
        var result = new DescendantVerificationResult(42, true, 5, []);

        var callResult = McpResultBuilder.FormatVerification(result, "org/proj");
        var root = ParseJson(callResult);

        root.GetProperty("rootId").GetInt32().ShouldBe(42);
        root.GetProperty("verified").GetBoolean().ShouldBeTrue();
        root.GetProperty("totalChecked").GetInt32().ShouldBe(5);
        root.GetProperty("incompleteCount").GetInt32().ShouldBe(0);
        root.GetProperty("incomplete").GetArrayLength().ShouldBe(0);
        root.GetProperty("workspace").GetString().ShouldBe("org/proj");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Verified = false — populated incomplete with all required fields
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatVerification_WithIncompleteItems_SerializesAllFields()
    {
        var incomplete = new List<IncompleteItem>
        {
            new(101, "Task A", "Task", "Active", 42, 1),
            new(102, "Bug B", "Bug", "New", 101, 2),
        };
        var result = new DescendantVerificationResult(42, false, 10, incomplete);

        var callResult = McpResultBuilder.FormatVerification(result, null);
        var root = ParseJson(callResult);

        root.GetProperty("rootId").GetInt32().ShouldBe(42);
        root.GetProperty("verified").GetBoolean().ShouldBeFalse();
        root.GetProperty("totalChecked").GetInt32().ShouldBe(10);
        root.GetProperty("incompleteCount").GetInt32().ShouldBe(2);

        var items = root.GetProperty("incomplete");
        items.GetArrayLength().ShouldBe(2);

        var first = items[0];
        first.GetProperty("id").GetInt32().ShouldBe(101);
        first.GetProperty("title").GetString().ShouldBe("Task A");
        first.GetProperty("type").GetString().ShouldBe("Task");
        first.GetProperty("state").GetString().ShouldBe("Active");
        first.GetProperty("parentId").GetInt32().ShouldBe(42);
        first.GetProperty("depth").GetInt32().ShouldBe(1);

        var second = items[1];
        second.GetProperty("id").GetInt32().ShouldBe(102);
        second.GetProperty("parentId").GetInt32().ShouldBe(101);
    }

    // ═══════════════════════════════════════════════════════════════
    //  incompleteCount <-> incomplete.Length consistency
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void FormatVerification_IncompleteCount_MatchesArrayLength(int count)
    {
        var incomplete = Enumerable.Range(1, count)
            .Select(i => new IncompleteItem(i, $"Item {i}", "Task", "Active", null, 1))
            .ToList();
        var result = new DescendantVerificationResult(1, count == 0, count, incomplete);

        var callResult = McpResultBuilder.FormatVerification(result, "org/proj");
        var root = ParseJson(callResult);

        var incompleteCount = root.GetProperty("incompleteCount").GetInt32();
        var arrayLength = root.GetProperty("incomplete").GetArrayLength();
        incompleteCount.ShouldBe(arrayLength);
        incompleteCount.ShouldBe(count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nullable parentId serialization
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatVerification_NullParentId_WritesJsonNull()
    {
        var incomplete = new List<IncompleteItem>
        {
            new(200, "Orphan", "Task", "Active", null, 0),
        };
        var result = new DescendantVerificationResult(10, false, 1, incomplete);

        var callResult = McpResultBuilder.FormatVerification(result, null);
        var root = ParseJson(callResult);

        var item = root.GetProperty("incomplete")[0];
        item.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nullable workspace serialization
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FormatVerification_NullWorkspace_WritesJsonNull()
    {
        var result = new DescendantVerificationResult(1, true, 0, []);

        var callResult = McpResultBuilder.FormatVerification(result, null);
        var root = ParseJson(callResult);

        root.GetProperty("workspace").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement ParseJson(CallToolResult result)
    {
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text!;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
