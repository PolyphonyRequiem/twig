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
/// MCP tools for read-only queries: twig.tree, twig.workspace.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(
    IWorkItemRepository workItemRepo,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
    TwigConfiguration config)
{
    [McpServerTool(Name = "twig.tree"), Description("Display work item hierarchy as a tree")]
    public async Task<CallToolResult> Tree(
        [Description("Max child depth to display")] int? depth = null,
        CancellationToken ct = default)
    {
        var resolveResult = await activeItemResolver.GetActiveItemAsync(ct);

        if (resolveResult is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig.set first.");
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
}
