using ModelContextProtocol.Protocol;

namespace Twig.Mcp.Services.Batch;

/// <summary>
/// Abstraction for routing a tool name + args dictionary to the corresponding MCP tool method.
/// Enables the <c>BatchExecutionEngine</c> to be tested in isolation from MCP transport (NFR-7).
/// </summary>
public interface IToolDispatcher
{
    /// <summary>
    /// Dispatches a single tool call by name, extracting typed parameters from the args dictionary.
    /// </summary>
    /// <param name="toolName">The MCP tool name (e.g. <c>twig_set</c>).</param>
    /// <param name="args">Argument dictionary with scalar values parsed from JSON.</param>
    /// <param name="workspaceOverride">Batch-level workspace override; used when the step has no explicit <c>workspace</c> arg.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="CallToolResult"/> from the invoked tool method.</returns>
    Task<CallToolResult> DispatchAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        string? workspaceOverride,
        CancellationToken ct);
}
