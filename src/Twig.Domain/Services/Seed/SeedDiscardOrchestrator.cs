using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Builds a cascade-discard plan by traversing the seed parent→child graph via BFS.
/// 1 dependency on <see cref="IWorkItemRepository"/>, consumed by <c>SeedDiscardCommand</c>.
/// </summary>
public sealed class SeedDiscardOrchestrator(IWorkItemRepository workItemRepo)
{
    /// <summary>
    /// Validates the target seed exists and is a seed, then performs a BFS traversal
    /// of the seed graph to collect all descendant seed IDs.
    /// Returns <c>null</c> if the seed is not found or the item is not a seed.
    /// </summary>
    public async Task<SeedDiscardPlan?> BuildDiscardPlanAsync(int seedId, CancellationToken ct = default)
    {
        var target = await workItemRepo.GetByIdAsync(seedId, ct);
        if (target is null || !target.IsSeed)
            return null;

        var allSeeds = await workItemRepo.GetSeedsAsync(ct);

        // Build parent → children lookup (only seeds)
        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var seed in allSeeds)
        {
            if (seed.ParentId is not { } parentId)
                continue;

            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                children = [];
                childrenByParent[parentId] = children;
            }

            children.Add(seed.Id);
        }

        // BFS from target to collect all descendants
        var allIds = new List<int> { seedId };
        var queue = new Queue<int>();
        queue.Enqueue(seedId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!childrenByParent.TryGetValue(current, out var children))
                continue;

            foreach (var childId in children)
            {
                allIds.Add(childId);
                queue.Enqueue(childId);
            }
        }

        return new SeedDiscardPlan
        {
            TargetId = seedId,
            TargetTitle = target.Title,
            AllIds = allIds,
        };
    }
}
