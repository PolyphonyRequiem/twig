using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for read-only queries: twig_tree, twig_workspace.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    IIterationService iterationService,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
    TwigConfiguration config)
{
    [McpServerTool(Name = "twig_tree"), Description("Display work item hierarchy as a tree")]
    public async Task<CallToolResult> Tree(
        [Description("Max child depth to display")] int? depth = null,
        CancellationToken ct = default)
    {
        var resolveResult = await activeItemResolver.GetActiveItemAsync(ct);

        if (resolveResult is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set first.");
        if (resolveResult is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} unreachable: {u.Reason}");

        var item = resolveResult is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolveResult).WorkItem;

        // Build parent chain
        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct)
            : Array.Empty<WorkItem>();

        var maxChildren = depth ?? config.Display.TreeDepth;
        var allChildren = await workItemRepo.GetChildrenAsync(item.Id, ct);
        var totalChildCount = allChildren.Count;
        var children = allChildren.Count > maxChildren
            ? allChildren.Take(maxChildren).ToList()
            : allChildren;

        // Compute sibling counts for parent chain + focused item
        var siblingCounts = new Dictionary<int, int?>();
        foreach (var node in parentChain.Append(item))
            siblingCounts[node.Id] = node.ParentId.HasValue
                ? (await workItemRepo.GetChildrenAsync(node.ParentId.Value, ct)).Count
                : null;

        // Best-effort link sync
        IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
        try
        {
            links = await syncCoordinator.SyncLinksAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var tree = WorkTree.Build(item, parentChain, children, siblingCounts, links);

        return McpResultBuilder.FormatTree(tree, totalChildCount);
    }

    [McpServerTool(Name = "twig_workspace"), Description("Returns the current sprint workspace: active context item, sprint backlog items, and seeds.")]
    public async Task<CallToolResult> Workspace(
        [Description("Show all team items instead of just the current user")] bool all = false,
        CancellationToken ct = default)
    {
        // 1. Context item (nullable — no error if absent)
        var contextId = await contextStore.GetActiveWorkItemIdAsync(ct);
        WorkItem? contextItem = contextId.HasValue
            ? await workItemRepo.GetByIdAsync(contextId.Value, ct)
            : null;

        // 2. Current iteration
        var iteration = await iterationService.GetCurrentIterationAsync(ct);

        // 3. Sprint items — filter by user when all=false and display name is configured
        var sprintItems = !all && config.User.DisplayName is not null
            ? await workItemRepo.GetByIterationAndAssigneeAsync(iteration, config.User.DisplayName, ct)
            : await workItemRepo.GetByIterationAsync(iteration, ct);

        // 4. Seeds
        var seeds = await workItemRepo.GetSeedsAsync(ct);

        // 5. Build workspace
        var workspace = Domain.ReadModels.Workspace.Build(contextItem, sprintItems, seeds);

        // 6. Format result
        return McpResultBuilder.FormatWorkspace(workspace, config.Seed.StaleDays);
    }
}
