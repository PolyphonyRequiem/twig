using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Static helper for recursively fetching work item descendants from the local cache.
/// </summary>
public static class WorkTreeFetcher
{
    /// <summary>
    /// Recursively fetches children for each item in <paramref name="parents"/> up to
    /// <paramref name="remainingDepth"/> levels, accumulating results into <paramref name="result"/>
    /// keyed by parent ID. Stops when depth is exhausted or a node has no children.
    /// </summary>
    public static async Task FetchDescendantsAsync(
        IWorkItemRepository repo,
        IReadOnlyList<WorkItem> parents,
        int remainingDepth,
        Dictionary<int, IReadOnlyList<WorkItem>> result,
        CancellationToken ct = default)
    {
        if (remainingDepth <= 0 || parents.Count == 0)
            return;

        foreach (var parent in parents)
        {
            var children = await repo.GetChildrenAsync(parent.Id, ct);
            if (children.Count > 0)
            {
                result[parent.Id] = children;
                await FetchDescendantsAsync(repo, children, remainingDepth - 1, result, ct);
            }
        }
    }

    /// <summary>
    /// Recursively fetches children for each item in <paramref name="parents"/> up to
    /// <paramref name="remainingDepth"/> levels using a delegate, allowing callers to inject
    /// cache-first/ADO-fallback fetch logic. Accumulates results into <paramref name="result"/>
    /// keyed by parent ID.
    /// </summary>
    public static async Task FetchDescendantsAsync(
        Func<int, CancellationToken, Task<IReadOnlyList<WorkItem>>> fetchChildren,
        IReadOnlyList<WorkItem> parents,
        int remainingDepth,
        Dictionary<int, IReadOnlyList<WorkItem>> result,
        CancellationToken ct = default)
    {
        if (remainingDepth <= 0 || parents.Count == 0)
            return;

        foreach (var parent in parents)
        {
            var children = await fetchChildren(parent.Id, ct);
            if (children.Count > 0)
            {
                result[parent.Id] = children;
                await FetchDescendantsAsync(fetchChildren, children, remainingDepth - 1, result, ct);
            }
        }
    }
}
