using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Config;
using ModelContextProtocol.Protocol;
using Twig.Domain.Interfaces;

namespace Twig.Mcp.Services;

/// <summary>
/// Generates contextual hints for MCP tool responses.
/// Hints are only computed when <c>verbose=true</c> is passed to a tool,
/// keeping the default response lightweight for batch/automated scenarios.
/// </summary>
internal static class McpHintProvider
{
    /// <summary>
    /// Inspects workspace state and returns actionable hints for the caller.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetHintsAsync(
        ConnectionScope ctx, CancellationToken ct)
    {
        var hints = new List<string>();

        // 1. Pending changes on the active item
        var activeId = await ctx.Get<IContextStore>().GetActiveWorkItemIdAsync(ct);
        if (activeId.HasValue)
        {
            var changes = await ctx.Get<IPendingChangeStore>().GetChangesAsync(activeId.Value, ct);
            if (changes.Count > 0)
            {
                var noun = changes.Count == 1 ? "change" : "changes";
                hints.Add($"item has {changes.Count} pending {noun} — consider twig_sync");
            }
        }

        // 2. Dirty items across the workspace
        var dirtyIds = await ctx.Get<IPendingChangeStore>().GetDirtyItemIdsAsync(ct);
        // Exclude the active item to avoid duplicate messaging
        var otherDirtyCount = activeId.HasValue
            ? dirtyIds.Count(id => id != activeId.Value)
            : dirtyIds.Count;
        if (otherDirtyCount > 0)
        {
            var noun = otherDirtyCount == 1 ? "item" : "items";
            hints.Add($"{otherDirtyCount} other dirty {noun} in workspace — consider twig_sync");
        }

        // 3. Unpublished seeds
        var seeds = await ctx.Get<IWorkItemRepository>().GetSeedsAsync(ct);
        if (seeds.Count > 0)
        {
            var noun = seeds.Count == 1 ? "seed" : "seeds";
            hints.Add($"{seeds.Count} unpublished {noun}");
        }

        return hints;
    }

    /// <summary>
    /// Conditionally appends hints to the result when verbose is true.
    /// When verbose is false, returns the result unchanged (no hints array).
    /// </summary>
    public static async Task<CallToolResult> ApplyHintsAsync(
        CallToolResult result, bool verbose, ConnectionScope ctx, CancellationToken ct)
    {
        if (!verbose) return result;
        var hints = await GetHintsAsync(ctx, ct);
        return McpResultBuilder.WithHints(result, hints);
    }
}
