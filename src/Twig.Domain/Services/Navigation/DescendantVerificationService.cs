using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Recursively verifies that all descendants of a root work item are in terminal states
/// (Completed, Resolved, or Removed). Uses ADO-first with cache-fallback at each level.
/// </summary>
public sealed class DescendantVerificationService(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider)
{
    /// <summary>
    /// Traverses children of <paramref name="rootId"/> up to <paramref name="maxDepth"/> levels,
    /// returning a result indicating whether all descendants are in terminal states.
    /// The root item itself is NOT included in TotalChecked or Incomplete.
    /// </summary>
    public async Task<DescendantVerificationResult> VerifyAsync(
        int rootId,
        int maxDepth = 2,
        CancellationToken ct = default)
    {
        var processConfig = processConfigProvider.GetConfiguration();
        var incomplete = new List<IncompleteItem>();
        var totalChecked = 0;

        // BFS traversal: queue of (parentId, currentDepth)
        var queue = new Queue<(int ParentId, int Depth)>();
        queue.Enqueue((rootId, 1));

        while (queue.Count > 0)
        {
            var (parentId, depth) = queue.Dequeue();
            if (depth > maxDepth)
                continue;

            var children = await FetchChildrenWithFallbackAsync(parentId, ct);

            foreach (var child in children)
            {
                totalChecked++;

                var isTerminal = IsTerminalState(child, processConfig);

                if (!isTerminal)
                {
                    incomplete.Add(new IncompleteItem(
                        child.Id,
                        child.Title,
                        child.Type.Value,
                        child.State,
                        child.ParentId,
                        depth));
                }

                // Continue traversal into children regardless of terminal state
                if (depth < maxDepth)
                {
                    queue.Enqueue((child.Id, depth + 1));
                }
            }
        }

        return new DescendantVerificationResult(
            rootId,
            incomplete.Count == 0,
            totalChecked,
            incomplete);
    }

    /// <summary>
    /// Tries ADO first, falls back to local cache on failure.
    /// </summary>
    private async Task<IReadOnlyList<WorkItem>> FetchChildrenWithFallbackAsync(
        int parentId,
        CancellationToken ct)
    {
        try
        {
            return await adoService.FetchChildrenAsync(parentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await workItemRepo.GetChildrenAsync(parentId, ct);
        }
    }

    /// <summary>
    /// Determines if a work item is in a terminal state (Completed, Resolved, or Removed).
    /// Items with unmapped types are treated as non-terminal (conservative).
    /// </summary>
    private static bool IsTerminalState(WorkItem item, ProcessConfiguration processConfig)
    {
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return false; // unmapped type → conservative non-terminal

        var category = StateCategoryResolver.Resolve(item.State, typeConfig.StateEntries);
        return category is StateCategory.Completed or StateCategory.Resolved or StateCategory.Removed;
    }
}
