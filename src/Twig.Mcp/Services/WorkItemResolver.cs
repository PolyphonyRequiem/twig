using ModelContextProtocol.Protocol;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Navigation;

namespace Twig.Mcp.Services;

/// <summary>
/// Shared helper for resolving a work item by optional ID across all MCP tool classes.
/// When <paramref name="id"/> is provided, loads the item from cache (with ADO fallback)
/// without changing the active context. When omitted, falls back to the active context.
/// </summary>
internal static class WorkItemResolver
{
    /// <summary>
    /// Resolves a work item either by explicit ID (cache+ADO fallback, no context change)
    /// or via the active item resolver (current active context).
    /// </summary>
    /// <param name="ctx">The workspace context providing cache, ADO, and active-item services.</param>
    /// <param name="id">Optional work item ID. When provided, resolves directly without touching context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the resolved <see cref="WorkItem"/> and an optional <see cref="CallToolResult"/> error.
    /// When <c>Error</c> is non-null, <c>Item</c> is null and the caller should return the error immediately.
    /// </returns>
    public static async Task<(WorkItem? Item, CallToolResult? Error)> ResolveWorkItemAsync(
        WorkspaceContext ctx, int? id, CancellationToken ct)
    {
        if (id.HasValue)
        {
            var (item, error) = await ctx.FetchWithFallbackAsync(id.Value, ct);
            if (item is null)
                return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, error ?? $"Work item #{id.Value} not found.", ctx, ct));
            return (item, null);
        }

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveNoContext)
            return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.NoContext, "No active work item. Use twig_set to set context.", ctx, ct));
        if (resolved is ActiveUnreachable u)
            return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Work item #{u.Id} unreachable: {u.Reason}", ctx, ct));

        var activeItem = resolved switch
        {
            Found f => f.WorkItem,
            FetchedFromAdo a => a.WorkItem,
            _ => throw new InvalidOperationException("Unexpected active item result"),
        };
        return (activeItem, null);
    }
}
