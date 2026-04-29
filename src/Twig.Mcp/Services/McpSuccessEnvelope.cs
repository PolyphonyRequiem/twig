using System.Text.Json;

namespace Twig.Mcp.Services;

/// <summary>
/// Contract type for a successful MCP tool response envelope.
/// Every successful tool response is wrapped in this shape:
/// <code>{ "success": true, "data": { ... }, "context": { ... }, "hints": [ ... ] }</code>.
/// </summary>
/// <param name="Success">Always <c>true</c> for success envelopes.</param>
/// <param name="Data">
/// Tool-specific payload. The shape varies per tool but is always a JSON object.
/// Represented as <see cref="JsonElement"/> to support deserialization of any tool's response.
/// </param>
/// <param name="Context">
/// Contextual metadata automatically populated by <see cref="EnvelopeBuilder"/>
/// from the current workspace state.
/// </param>
/// <param name="Hints">
/// Actionable suggestions for the calling agent (e.g. "item has 3 pending changes — consider twig_sync").
/// Empty array when no hints apply or when <c>verbose</c> is <c>false</c>.
/// </param>
public sealed record McpSuccessEnvelope(
    bool Success,
    JsonElement Data,
    McpContext Context,
    IReadOnlyList<string> Hints);
