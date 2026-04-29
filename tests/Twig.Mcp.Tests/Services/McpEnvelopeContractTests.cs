using System.Text.Json;
using Shouldly;
using Twig.Mcp.Serialization;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

public sealed class McpEnvelopeContractTests
{
    // ── McpSuccessEnvelope ──────────────────────────────────────────

    [Fact]
    public void McpSuccessEnvelope_RoundTrips_ViaSourceGen()
    {
        var context = new McpContext(42, "org/proj", "PT5M");
        var data = JsonDocument.Parse("""{"id":42,"title":"Test"}""").RootElement;
        var envelope = new McpSuccessEnvelope(true, data, context, ["hint1", "hint2"]);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpSuccessEnvelope);
        var deserialized = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpSuccessEnvelope)!;

        deserialized.Success.ShouldBeTrue();
        deserialized.Data.GetProperty("id").GetInt32().ShouldBe(42);
        deserialized.Data.GetProperty("title").GetString().ShouldBe("Test");
        deserialized.Context.ActiveItemId.ShouldBe(42);
        deserialized.Context.Workspace.ShouldBe("org/proj");
        deserialized.Context.CacheAge.ShouldBe("PT5M");
        deserialized.Hints.Count.ShouldBe(2);
        deserialized.Hints[0].ShouldBe("hint1");
        deserialized.Hints[1].ShouldBe("hint2");
    }

    [Fact]
    public void McpSuccessEnvelope_EmptyHints_SerializesAsEmptyArray()
    {
        var envelope = new McpSuccessEnvelope(
            true,
            JsonDocument.Parse("{}").RootElement,
            new McpContext(null, "", ""),
            []);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpSuccessEnvelope);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("hints").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void McpSuccessEnvelope_NullActiveItemId_SerializedCorrectly()
    {
        var envelope = new McpSuccessEnvelope(
            true,
            JsonDocument.Parse("""{"ok":true}""").RootElement,
            new McpContext(null, "org/proj", ""),
            []);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpSuccessEnvelope);
        using var doc = JsonDocument.Parse(json);

        // activeItemId should be omitted (WhenWritingNull) or null
        var context = doc.RootElement.GetProperty("context");
        if (context.TryGetProperty("activeItemId", out var activeId))
            activeId.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void McpSuccessEnvelope_RecordEquality_SameDataInstance()
    {
        // JsonElement is a struct without content-based equality,
        // so two envelopes sharing the same Data instance are equal.
        var data = JsonDocument.Parse("{}").RootElement;
        var ctx = new McpContext(1, "org/proj", "PT1M");
        IReadOnlyList<string> hints = ["hint"];
        var a = new McpSuccessEnvelope(true, data, ctx, hints);
        var b = new McpSuccessEnvelope(true, data, ctx, hints);
        a.ShouldBe(b);
    }

    // ── McpErrorEnvelope ───────────────────────────────────────────

    [Fact]
    public void McpErrorEnvelope_RoundTrips_ViaSourceGen()
    {
        var error = new McpError("ITEM_NOT_FOUND", "Not found.", new Dictionary<string, string> { ["id"] = "9999" });
        var context = new McpContext(null, "org/proj", "");
        var envelope = new McpErrorEnvelope(false, error, context);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);
        var deserialized = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpErrorEnvelope)!;

        deserialized.Success.ShouldBeFalse();
        deserialized.Error.Code.ShouldBe("ITEM_NOT_FOUND");
        deserialized.Error.Message.ShouldBe("Not found.");
        deserialized.Error.Details["id"].ShouldBe("9999");
        deserialized.Context.ShouldNotBeNull();
        deserialized.Context!.Workspace.ShouldBe("org/proj");
    }

    [Fact]
    public void McpErrorEnvelope_NullContext_OmittedInJson()
    {
        var error = new McpError("INTERNAL_ERROR", "Unexpected error.", new Dictionary<string, string>());
        var envelope = new McpErrorEnvelope(false, error, null);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("INTERNAL_ERROR");
        // Context should be omitted (WhenWritingNull)
        doc.RootElement.TryGetProperty("context", out _).ShouldBeFalse();
    }

    [Fact]
    public void McpErrorEnvelope_WithContext_IncludesContextInJson()
    {
        var error = new McpError("INVALID_INPUT", "Bad input.", new Dictionary<string, string>());
        var context = new McpContext(10, "org/proj", "PT2M");
        var envelope = new McpErrorEnvelope(false, error, context);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);
        using var doc = JsonDocument.Parse(json);

        var ctx = doc.RootElement.GetProperty("context");
        ctx.GetProperty("activeItemId").GetInt32().ShouldBe(10);
        ctx.GetProperty("workspace").GetString().ShouldBe("org/proj");
        ctx.GetProperty("cacheAge").GetString().ShouldBe("PT2M");
    }

    [Fact]
    public void McpErrorEnvelope_RecordEquality()
    {
        var details = new Dictionary<string, string> { ["key"] = "val" };
        var error = new McpError("CODE", "msg", details);
        var a = new McpErrorEnvelope(false, error, null);
        var b = new McpErrorEnvelope(false, error, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void McpErrorEnvelope_EmptyDetails_SerializesAsEmptyObject()
    {
        var error = new McpError("NO_CONTEXT", "No item set.", new Dictionary<string, string>());
        var envelope = new McpErrorEnvelope(false, error, null);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);
        using var doc = JsonDocument.Parse(json);

        var details = doc.RootElement.GetProperty("error").GetProperty("details");
        details.EnumerateObject().Count().ShouldBe(0);
    }

    // ── McpContext serialization ───────────────────────────────────

    [Fact]
    public void McpContext_RoundTrips_ViaSourceGen()
    {
        var context = new McpContext(42, "org/proj", "PT5M");

        var json = JsonSerializer.Serialize(context, McpJsonContext.Default.McpContext);
        var deserialized = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpContext)!;

        deserialized.ActiveItemId.ShouldBe(42);
        deserialized.Workspace.ShouldBe("org/proj");
        deserialized.CacheAge.ShouldBe("PT5M");
    }

    // ── McpError serialization ─────────────────────────────────────

    [Fact]
    public void McpError_RoundTrips_ViaSourceGen()
    {
        var details = new Dictionary<string, string> { ["field"] = "System.Title" };
        var error = new McpError("INVALID_INPUT", "Missing field.", details);

        var json = JsonSerializer.Serialize(error, McpJsonContext.Default.McpError);
        var deserialized = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpError)!;

        deserialized.Code.ShouldBe("INVALID_INPUT");
        deserialized.Message.ShouldBe("Missing field.");
        deserialized.Details["field"].ShouldBe("System.Title");
    }

    // ── Deserialization from EnvelopeBuilder output ─────────────────

    [Fact]
    public void McpSuccessEnvelope_DeserializesEnvelopeBuilderOutput()
    {
        // Simulates the JSON shape produced by EnvelopeBuilder.SuccessAsync
        var json = """
            {
                "success": true,
                "data": { "id": 42, "title": "Test Item" },
                "context": {
                    "activeItemId": 42,
                    "workspace": "org/proj",
                    "cacheAge": "PT2M30S"
                },
                "hints": ["item has 3 pending changes"]
            }
            """;

        var envelope = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpSuccessEnvelope)!;

        envelope.Success.ShouldBeTrue();
        envelope.Data.GetProperty("id").GetInt32().ShouldBe(42);
        envelope.Context.ActiveItemId.ShouldBe(42);
        envelope.Context.CacheAge.ShouldBe("PT2M30S");
        envelope.Hints.Count.ShouldBe(1);
        envelope.Hints[0].ShouldBe("item has 3 pending changes");
    }

    [Fact]
    public void McpErrorEnvelope_DeserializesEnvelopeBuilderOutput()
    {
        // Simulates the JSON shape produced by EnvelopeBuilder.Error
        var json = """
            {
                "success": false,
                "error": {
                    "code": "ITEM_NOT_FOUND",
                    "message": "Work item 9999 not found.",
                    "details": {}
                }
            }
            """;

        var envelope = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpErrorEnvelope)!;

        envelope.Success.ShouldBeFalse();
        envelope.Error.Code.ShouldBe("ITEM_NOT_FOUND");
        envelope.Error.Message.ShouldBe("Work item 9999 not found.");
        envelope.Error.Details.Count.ShouldBe(0);
        envelope.Context.ShouldBeNull();
    }

    [Fact]
    public void McpErrorEnvelope_DeserializesErrorAsyncOutput_WithContext()
    {
        // Simulates the JSON shape produced by EnvelopeBuilder.ErrorAsync
        var json = """
            {
                "success": false,
                "error": {
                    "code": "INVALID_INPUT",
                    "message": "Bad input",
                    "details": {}
                },
                "context": {
                    "activeItemId": 10,
                    "workspace": "org/proj",
                    "cacheAge": ""
                }
            }
            """;

        var envelope = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpErrorEnvelope)!;

        envelope.Success.ShouldBeFalse();
        envelope.Error.Code.ShouldBe("INVALID_INPUT");
        envelope.Context.ShouldNotBeNull();
        envelope.Context!.ActiveItemId.ShouldBe(10);
    }
}
