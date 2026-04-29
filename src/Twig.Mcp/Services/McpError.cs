using System.Text.Json.Serialization;

namespace Twig.Mcp.Services;

/// <summary>
/// Structured error information for MCP envelope error responses.
/// </summary>
/// <param name="Code">Machine-readable error code from <see cref="McpErrorCode"/>.</param>
/// <param name="Message">Human-readable error message.</param>
/// <param name="Details">
/// Optional dictionary of additional details about the error.
/// Empty dictionary when no additional details are available.
/// </param>
public sealed record McpError(
    string Code,
    string Message,
    [property: JsonPropertyName("details")]
    IReadOnlyDictionary<string, string> Details);
