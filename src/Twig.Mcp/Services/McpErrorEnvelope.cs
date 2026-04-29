namespace Twig.Mcp.Services;

/// <summary>
/// Contract type for a failed MCP tool response envelope.
/// Every error response follows this shape:
/// <code>{ "success": false, "error": { "code": "...", "message": "...", "details": { ... } }, "context"?: { ... } }</code>.
/// </summary>
/// <param name="Success">Always <c>false</c> for error envelopes.</param>
/// <param name="Error">Structured error with machine-readable code, human-readable message, and optional details.</param>
/// <param name="Context">
/// Contextual metadata when workspace state is available at the point of failure.
/// <c>null</c> when the error occurs before workspace resolution (e.g. workspace not found).
/// </param>
public sealed record McpErrorEnvelope(
    bool Success,
    McpError Error,
    McpContext? Context);
