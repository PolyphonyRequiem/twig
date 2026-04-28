using System.Text.Json;
using ModelContextProtocol.Protocol;
using Shouldly;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpResultBuilderLinkBatchTests
{
    // ── Helper ──────────────────────────────────────────────────────

    private static JsonElement ParseJson(CallToolResult result)
    {
        var json = ((TextContentBlock)result.Content![0]).Text;
        return JsonDocument.Parse(json).RootElement;
    }

    // ── All-success ─────────────────────────────────────────────────

    [Fact]
    public void FormatLinkBatch_AllSuccess_ReturnsCorrectShape()
    {
        var results = new List<LinkBatchItemResult>
        {
            new(42, "parent", true),
            new(43, "reparent", true),
        };

        var result = McpResultBuilder.FormatLinkBatch(results);

        result.IsError.ShouldNotBe(true);

        var root = ParseJson(result);
        root.GetProperty("totalOperations").GetInt32().ShouldBe(2);
        root.GetProperty("succeeded").GetInt32().ShouldBe(2);
        root.GetProperty("failed").GetInt32().ShouldBe(0);

        var ops = root.GetProperty("operations");
        ops.GetArrayLength().ShouldBe(2);

        var op0 = ops[0];
        op0.GetProperty("itemId").GetInt32().ShouldBe(42);
        op0.GetProperty("op").GetString().ShouldBe("parent");
        op0.GetProperty("success").GetBoolean().ShouldBeTrue();
        op0.TryGetProperty("error", out _).ShouldBeFalse();

        var op1 = ops[1];
        op1.GetProperty("itemId").GetInt32().ShouldBe(43);
        op1.GetProperty("op").GetString().ShouldBe("reparent");
        op1.GetProperty("success").GetBoolean().ShouldBeTrue();
        op1.TryGetProperty("error", out _).ShouldBeFalse();
    }

    // ── All-failure ─────────────────────────────────────────────────

    [Fact]
    public void FormatLinkBatch_AllFailure_ReturnsCorrectShape()
    {
        var results = new List<LinkBatchItemResult>
        {
            new(44, "artifact", false, "URL invalid"),
            new(45, "parent", false, "Item not found"),
        };

        var result = McpResultBuilder.FormatLinkBatch(results);

        result.IsError.ShouldNotBe(true);

        var root = ParseJson(result);
        root.GetProperty("totalOperations").GetInt32().ShouldBe(2);
        root.GetProperty("succeeded").GetInt32().ShouldBe(0);
        root.GetProperty("failed").GetInt32().ShouldBe(2);

        var ops = root.GetProperty("operations");
        ops[0].GetProperty("error").GetString().ShouldBe("URL invalid");
        ops[1].GetProperty("error").GetString().ShouldBe("Item not found");
    }

    // ── Mixed results ───────────────────────────────────────────────

    [Fact]
    public void FormatLinkBatch_MixedResults_SummaryCountsCorrect()
    {
        var results = new List<LinkBatchItemResult>
        {
            new(42, "parent", true),
            new(43, "reparent", true),
            new(44, "artifact", false, "URL invalid"),
        };

        var result = McpResultBuilder.FormatLinkBatch(results);

        result.IsError.ShouldNotBe(true);

        var root = ParseJson(result);
        root.GetProperty("totalOperations").GetInt32().ShouldBe(3);
        root.GetProperty("succeeded").GetInt32().ShouldBe(2);
        root.GetProperty("failed").GetInt32().ShouldBe(1);

        var ops = root.GetProperty("operations");
        ops.GetArrayLength().ShouldBe(3);

        // Successful entry omits error
        ops[0].GetProperty("success").GetBoolean().ShouldBeTrue();
        ops[0].TryGetProperty("error", out _).ShouldBeFalse();

        // Failed entry has error
        ops[2].GetProperty("success").GetBoolean().ShouldBeFalse();
        ops[2].GetProperty("error").GetString().ShouldBe("URL invalid");
    }

    // ── Empty list ──────────────────────────────────────────────────

    [Fact]
    public void FormatLinkBatch_EmptyList_ReturnsZeroCountsAndEmptyArray()
    {
        var results = new List<LinkBatchItemResult>();

        var result = McpResultBuilder.FormatLinkBatch(results);

        result.IsError.ShouldNotBe(true);

        var root = ParseJson(result);
        root.GetProperty("totalOperations").GetInt32().ShouldBe(0);
        root.GetProperty("succeeded").GetInt32().ShouldBe(0);
        root.GetProperty("failed").GetInt32().ShouldBe(0);
        root.GetProperty("operations").GetArrayLength().ShouldBe(0);
    }

    // ── Error key omitted on success ────────────────────────────────

    [Fact]
    public void FormatLinkBatch_SuccessWithNullError_OmitsErrorKey()
    {
        var results = new List<LinkBatchItemResult>
        {
            new(99, "unparent", true, null),
        };

        var result = McpResultBuilder.FormatLinkBatch(results);
        var root = ParseJson(result);

        var op = root.GetProperty("operations")[0];
        op.GetProperty("success").GetBoolean().ShouldBeTrue();
        op.TryGetProperty("error", out _).ShouldBeFalse();
    }
}
