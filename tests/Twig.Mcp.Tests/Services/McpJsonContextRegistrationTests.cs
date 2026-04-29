using System.Text.Json;
using Shouldly;
using Twig.Mcp.Serialization;
using Twig.Mcp.Services;
using Xunit;

namespace Twig.Mcp.Tests.Services;

/// <summary>
/// Verifies that all MCP envelope types are registered in <see cref="McpJsonContext"/>
/// for AOT-compatible source-generated serialization.
/// Ensures no runtime reflection is needed for envelope serialization.
/// </summary>
public sealed class McpJsonContextRegistrationTests
{
    [Fact]
    public void McpSuccessEnvelope_IsRegistered()
    {
        McpJsonContext.Default.McpSuccessEnvelope.ShouldNotBeNull(
            "McpSuccessEnvelope must be registered in McpJsonContext for AOT serialization");
    }

    [Fact]
    public void McpErrorEnvelope_IsRegistered()
    {
        McpJsonContext.Default.McpErrorEnvelope.ShouldNotBeNull(
            "McpErrorEnvelope must be registered in McpJsonContext for AOT serialization");
    }

    [Fact]
    public void McpContext_IsRegistered()
    {
        McpJsonContext.Default.McpContext.ShouldNotBeNull(
            "McpContext must be registered in McpJsonContext for AOT serialization");
    }

    [Fact]
    public void McpError_IsRegistered()
    {
        McpJsonContext.Default.McpError.ShouldNotBeNull(
            "McpError must be registered in McpJsonContext for AOT serialization");
    }

    [Fact]
    public void McpSuccessEnvelope_CanSerialize_ViaSourceGen()
    {
        var data = JsonDocument.Parse("""{"id":1}""").RootElement;
        var envelope = new McpSuccessEnvelope(true, data, new McpContext(1, "org/proj", "PT1M"), []);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpSuccessEnvelope);

        json.ShouldNotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void McpErrorEnvelope_CanSerialize_ViaSourceGen()
    {
        var error = new McpError("TEST_ERROR", "test", new Dictionary<string, string>());
        var envelope = new McpErrorEnvelope(false, error, null);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);

        json.ShouldNotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void McpContext_CanSerialize_ViaSourceGen()
    {
        var context = new McpContext(42, "org/proj", "PT5M");

        var json = JsonSerializer.Serialize(context, McpJsonContext.Default.McpContext);

        json.ShouldNotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("activeItemId").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void McpError_CanSerialize_ViaSourceGen()
    {
        var error = new McpError("CODE", "message", new Dictionary<string, string> { ["k"] = "v" });

        var json = JsonSerializer.Serialize(error, McpJsonContext.Default.McpError);

        json.ShouldNotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("CODE");
    }

    [Fact]
    public void McpJsonContext_UsesCamelCaseNaming()
    {
        var context = new McpContext(1, "org/proj", "PT1M");
        var json = JsonSerializer.Serialize(context, McpJsonContext.Default.McpContext);

        // Property names should be camelCase (starts with lowercase)
        json.ShouldContain("\"activeItemId\"");
        json.ShouldContain("\"workspace\"");
        json.ShouldContain("\"cacheAge\"");

        // Verify PascalCase is NOT used (exact string match, case-sensitive)
        json.IndexOf("\"ActiveItemId\"", StringComparison.Ordinal).ShouldBe(-1,
            "Should use camelCase 'activeItemId', not PascalCase");
        json.IndexOf("\"Workspace\"", StringComparison.Ordinal).ShouldBe(-1,
            "Should use camelCase 'workspace', not PascalCase");
        json.IndexOf("\"CacheAge\"", StringComparison.Ordinal).ShouldBe(-1,
            "Should use camelCase 'cacheAge', not PascalCase");
    }

    [Fact]
    public void McpJsonContext_WhenWritingNull_OmitsNullValues()
    {
        var envelope = new McpErrorEnvelope(false,
            new McpError("CODE", "msg", new Dictionary<string, string>()),
            null);

        var json = JsonSerializer.Serialize(envelope, McpJsonContext.Default.McpErrorEnvelope);

        // WhenWritingNull means the "context" key won't appear
        json.ShouldNotContain("\"context\"");
    }

    [Fact]
    public void McpSuccessEnvelope_CanDeserialize_ViaSourceGen()
    {
        var json = """{"success":true,"data":{"id":1},"context":{"activeItemId":1,"workspace":"a/b","cacheAge":"PT0S"},"hints":[]}""";

        var envelope = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpSuccessEnvelope);

        envelope.ShouldNotBeNull();
        envelope.Success.ShouldBeTrue();
        envelope.Data.GetProperty("id").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void McpErrorEnvelope_CanDeserialize_ViaSourceGen()
    {
        var json = """{"success":false,"error":{"code":"X","message":"y","details":{}}}""";

        var envelope = JsonSerializer.Deserialize(json, McpJsonContext.Default.McpErrorEnvelope);

        envelope.ShouldNotBeNull();
        envelope.Success.ShouldBeFalse();
        envelope.Error.Code.ShouldBe("X");
    }
}
