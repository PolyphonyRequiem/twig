using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for read-only queries: twig_tree, twig_workspace.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_tree"), Description("Display work item hierarchy as a tree")]
    public async Task<CallToolResult> Tree(
        [Description("Max child depth to display")] int? depth = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var resolveResult = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);

        if (resolveResult is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set first.");
        if (resolveResult is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} unreachable: {u.Reason}");

        var item = resolveResult is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolveResult).WorkItem;

        // Build parent chain
        var parentChain = item.ParentId.HasValue
            ? await ctx.WorkItemRepo.GetParentChainAsync(item.ParentId.Value, ct)
            : Array.Empty<WorkItem>();

        var maxDepth = depth ?? ctx.Config.Display.TreeDepth;
        var allChildren = await ctx.WorkItemRepo.GetChildrenAsync(item.Id, ct);
        var totalChildCount = allChildren.Count;
        var children = allChildren;

        // Recursively fetch descendants up to maxChildren depth for deep tree output
        var descendantsByParentId = new Dictionary<int, IReadOnlyList<WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(ctx.WorkItemRepo, children, maxDepth - 1, descendantsByParentId, ct);

        // Compute sibling counts for parent chain + focused item
        var siblingCounts = new Dictionary<int, int?>();
        foreach (var node in parentChain.Append(item))
            siblingCounts[node.Id] = node.ParentId.HasValue
                ? (await ctx.WorkItemRepo.GetChildrenAsync(node.ParentId.Value, ct)).Count
                : null;

        // Best-effort link sync
        IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
        try
        {
            links = await ctx.SyncCoordinatorFactory.ReadOnly.SyncLinksAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var tree = WorkTree.Build(item, parentChain, children, siblingCounts, links, descendantsByParentId);

        return McpResultBuilder.FormatTree(tree, totalChildCount);
    }

    [McpServerTool(Name = "twig_workspace"), Description("Returns the current sprint workspace: active context item, sprint backlog items, and seeds.")]
    public async Task<CallToolResult> Workspace(
        [Description("Show all team items instead of just the current user")] bool all = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        // 1. Context item(nullable — no error if absent)
        var contextId = await ctx.ContextStore.GetActiveWorkItemIdAsync(ct);
        WorkItem? contextItem = contextId.HasValue
            ? await ctx.WorkItemRepo.GetByIdAsync(contextId.Value, ct)
            : null;

        // 2. Current iteration
        var iteration = await ctx.IterationService.GetCurrentIterationAsync(ct);

        // 3. Sprint items — filter by user when all=false and display name is configured
        var sprintItems = !all && ctx.Config.User.DisplayName is not null
            ? await ctx.WorkItemRepo.GetByIterationAndAssigneeAsync(iteration, ctx.Config.User.DisplayName, ct)
            : await ctx.WorkItemRepo.GetByIterationAsync(iteration, ct);

        // 4. Seeds
        var seeds = await ctx.WorkItemRepo.GetSeedsAsync(ct);

        // 5. Tracked items and exclusions
        var trackedItems = ctx.TrackingRepo is not null
            ? await ctx.TrackingRepo.GetAllTrackedAsync(ct)
            : Array.Empty<TrackedItem>();
        var excludedItems = ctx.TrackingRepo is not null
            ? await ctx.TrackingRepo.GetAllExcludedAsync(ct)
            : Array.Empty<ExcludedItem>();
        var excludedIds = excludedItems.Select(e => e.WorkItemId).ToList();

        // 6. Build workspace
        var ws = Domain.ReadModels.Workspace.Build(contextItem, sprintItems, seeds,
            trackedItems: trackedItems, excludedIds: excludedIds);

        // 7. Format result
        return McpResultBuilder.FormatWorkspace(ws, ctx.Config.Seed.StaleDays, ctx.Key.ToString(), excludedItems);
    }
}