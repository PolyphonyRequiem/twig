using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Config;
using ModelContextProtocol.Protocol;
using Twig.Domain.Aggregates;
using Twig.Domain.Services.Navigation;

namespace Twig.Mcp.Services;

/// <summary>
/// Shared helper for resolving a work item across all MCP tool classes.
/// </summary>
/// <remarks>
/// Two resolution paths exist, and the distinction is a correctness boundary rather than a
/// convenience:
/// <list type="bullet">
/// <item><description>
/// <see cref="ResolveExplicitAsync"/> takes a required id and never consults
/// <see cref="IContextStore"/>. <b>Every mutation must use this path.</b> The active work item
/// lives in one shared SQLite row read and written by both the CLI and the MCP server, so a
/// mutation that inferred its target from that row could land on an item the caller never named
/// — silently, and without either surface behaving incorrectly by its own contract.
/// </description></item>
/// <item><description>
/// <see cref="ResolveWorkItemAsync"/> retains the optional-id fallback for <b>read-only</b> tools
/// only, where a wrong target is visible in the response the model receives rather than being
/// written to Azure DevOps.
/// </description></item>
/// </list>
/// </remarks>
internal static class WorkItemResolver
{
    /// <summary>
    /// Resolves a work item by explicit ID (cache with ADO fallback). Never reads the active
    /// context, so the caller's target cannot be influenced by shared state another surface owns.
    /// </summary>
    /// <param name="ctx">The workspace context providing cache and ADO services.</param>
    /// <param name="id">The required work item ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the resolved <see cref="WorkItem"/> and an optional <see cref="CallToolResult"/>
    /// error. When <c>Error</c> is non-null, <c>Item</c> is null and the caller should return the
    /// error immediately.
    /// </returns>
    public static async Task<(WorkItem? Item, CallToolResult? Error)> ResolveExplicitAsync(
        ConnectionScope ctx, int id, CancellationToken ct)
    {
        // 🔴 Negative ids are NOT invalid. They are twig's display alias for a staged,
        // unpublished seed (0003/0014's identity model) — the same convention the tool
        // catalog encodes as `maximum: -1` on the seed-only tools. Rejecting them here
        // would break seed mutation across the whole MCP surface. Only 0 is meaningless:
        // it is neither a published ADO id nor a seed alias.
        if (id == 0)
        {
            return (null, await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.InvalidInput,
                "A work item ID is required; 0 is not a valid id (positive = published, negative = seed).",
                ctx,
                ct));
        }

        var (item, error) = await ctx.Get<WorkItemFetcher>().FetchWithFallbackAsync(id, ct);
        if (item is null)
        {
            return (null, await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.ItemNotFound, error ?? $"Work item #{id} not found.", ctx, ct));
        }

        return (item, null);
    }

    /// <summary>
    /// Resolves a work item either by explicit ID (cache+ADO fallback, no context change)
    /// or via the active item resolver (current active context).
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Read-only callers only.</b> Mutations must call <see cref="ResolveExplicitAsync"/>
    /// instead — see the type-level remarks for why the active-context fallback is unsafe as a
    /// mutation target.
    /// </remarks>
    /// <param name="ctx">The workspace context providing cache, ADO, and active-item services.</param>
    /// <param name="id">Optional work item ID. When provided, resolves directly without touching context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A tuple of the resolved <see cref="WorkItem"/> and an optional <see cref="CallToolResult"/> error.
    /// When <c>Error</c> is non-null, <c>Item</c> is null and the caller should return the error immediately.
    /// </returns>
    public static async Task<(WorkItem? Item, CallToolResult? Error)> ResolveWorkItemAsync(
        ConnectionScope ctx, int? id, CancellationToken ct)
    {
        if (id.HasValue)
            return await ResolveExplicitAsync(ctx, id.Value, ct);

        var resolved = await ctx.Get<ActiveItemResolver>().GetActiveItemAsync(ct);
        if (resolved is ActiveNoContext)
            return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.NoContext, "No active work item. Pass an explicit id.", ctx, ct));
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
