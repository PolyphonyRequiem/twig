using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for read-only queries: twig_tree, twig_workspace.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(WorkspaceResolver resolver, NavigationTools navigationTools)
{
    [McpServerTool(Name = "twig_tree"), Description("Display work item hierarchy as a tree")]
    public async Task<CallToolResult> Tree(
        [Description("Max child depth to display")] int? depth = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
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

        return await navigationTools.Show(item.Id, tree: true, depth: depth, workspace: workspace, verbose: verbose, ct: ct);
    }

    [McpServerTool(Name = "twig_workspace"), Description("Returns the current sprint workspace: active context item, sprint backlog items, and seeds. When tree=true, returns a tree-structured JSON with full backlog hierarchy.")]
    public async Task<CallToolResult> Workspace(
        [Description("Show all team items instead of just the current user")] bool all = false,
        [Description("When true, returns a tree-structured JSON response showing the full backlog hierarchy instead of the flat workspace view")] bool tree = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        // 1. Context item(nullable — no error if absent)
        var contextId = await ctx.ContextStore.GetActiveWorkItemIdAsync(ct);
        WorkItem? contextItem = contextId.HasValue
            ? await ctx.WorkItemRepo.GetByIdAsync(contextId.Value, ct)
            : null;

        // 2. Sprint items — use configured sprints when available, else fall back to current iteration
        var sprintEntries = ctx.Config.Workspace.Sprints;
        IReadOnlyList<WorkItem> sprintItems;

        if (sprintEntries is { Count: > 0 })
        {
            // Resolve configured sprint expressions via SprintIterationResolver
            var expressions = new List<IterationExpression>(sprintEntries.Count);
            foreach (var entry in sprintEntries)
            {
                var parseResult = IterationExpression.Parse(entry.Expression);
                if (parseResult.IsSuccess)
                    expressions.Add(parseResult.Value);
            }

            sprintItems = await ctx.SprintIterationResolver.GetSprintItemsAsync(
                expressions,
                ctx.Config.User.DisplayName,
                allUsers: all,
                ct);
        }
        else
        {
            // No configured sprints — fall back to current iteration
            var iteration = await ctx.IterationService.GetCurrentIterationAsync(ct);
            sprintItems = !all && ctx.Config.User.DisplayName is not null
                ? await ctx.WorkItemRepo.GetByIterationAndAssigneeAsync(iteration, ctx.Config.User.DisplayName, ct)
                : await ctx.WorkItemRepo.GetByIterationAsync(iteration, ct);
        }

        // 3. Seeds
        var seeds = await ctx.WorkItemRepo.GetSeedsAsync(ct);

        // 4. Tracked items and exclusions
        var trackedItems = ctx.TrackingRepo is not null
            ? await ctx.TrackingRepo.GetAllTrackedAsync(ct)
            : Array.Empty<TrackedItem>();
        var excludedItems = ctx.TrackingRepo is not null
            ? await ctx.TrackingRepo.GetAllExcludedAsync(ct)
            : Array.Empty<ExcludedItem>();
        var excludedIds = excludedItems.Select(e => e.WorkItemId).ToList();

        // 5. Build workspace
        var ws = Domain.ReadModels.Workspace.Build(contextItem, sprintItems, seeds,
            trackedItems: trackedItems, excludedIds: excludedIds);

        // 6. Tree mode — build WorkTree per sprint item and return tree-structured JSON
        if (tree)
        {
            var treeResult = await BuildWorkspaceTreeAsync(ctx, ws, excludedItems, ct);
            return await McpHintProvider.ApplyHintsAsync(treeResult, verbose, ctx, ct);
        }

        // 7. Format flat result
        var toolResult = McpResultBuilder.FormatWorkspace(ws, ctx.Config.Seed.StaleDays, ctx.Key.ToString(), excludedItems);
        return await McpHintProvider.ApplyHintsAsync(toolResult, verbose, ctx, ct);
    }

    private static async Task<CallToolResult> BuildWorkspaceTreeAsync(
        WorkspaceContext ctx,
        Domain.ReadModels.Workspace ws,
        IReadOnlyList<ExcludedItem> excludedItems,
        CancellationToken ct)
    {
        var maxDepth = ctx.Config.Display.TreeDepth;
        var roots = new List<(WorkTree Tree, int TotalChildren)>();

        foreach (var item in ws.SprintItems)
        {
            // Build parent chain
            var parentChain = item.ParentId.HasValue
                ? await ctx.WorkItemRepo.GetParentChainAsync(item.ParentId.Value, ct)
                : Array.Empty<WorkItem>();

            var allChildren = await ctx.FetchChildrenWithFallbackAsync(item.Id, ct);
            var totalChildCount = allChildren.Count;

            // Recursively fetch descendants up to maxDepth
            var descendantsByParentId = new Dictionary<int, IReadOnlyList<WorkItem>>();
            await WorkTreeFetcher.FetchDescendantsAsync(
                ctx.FetchChildrenWithFallbackAsync, allChildren, maxDepth - 1, descendantsByParentId, ct);

            var workTree = WorkTree.Build(item, parentChain, allChildren,
                descendantsByParentId: descendantsByParentId);
            roots.Add((workTree, totalChildCount));
        }

        return McpResultBuilder.FormatWorkspaceTree(roots, ws, ctx.Key.ToString(), excludedItems);
    }
}