using System.Text.Json.Serialization;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;

namespace Twig.Mcp.Serialization;

/// <summary>
/// Source-generated JSON serialization context for MCP envelope types.
/// Enables AOT-compatible serialization with no runtime reflection
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
/// Separate from <c>TwigJsonContext</c> because MCP types live in <c>Twig.Mcp</c>,
/// which is not referenced by <c>Twig.Infrastructure</c>.
/// </summary>
[JsonSerializable(typeof(McpSuccessEnvelope))]
[JsonSerializable(typeof(McpErrorEnvelope))]
[JsonSerializable(typeof(McpContext))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(BatchSummary))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpJsonContext : JsonSerializerContext;
