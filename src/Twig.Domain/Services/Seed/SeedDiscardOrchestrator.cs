using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Builds a cascade-discard plan and executes cascade deletion of seeds.
/// Consumed by <c>SeedDiscardCommand</c>.
/// </summary>
public sealed class SeedDiscardOrchestrator(
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
    IContextStore contextStore)
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

    /// <summary>
    /// Executes the cascade discard: clears active context if needed, deletes seed links,
    /// then deletes work item rows. Processes children before parents (reverse BFS order)
    /// to maintain referential integrity.
    /// </summary>
    public async Task ExecuteDiscardAsync(SeedDiscardPlan plan, CancellationToken ct = default)
    {
        // Clear active context if the current work item is any of the IDs being discarded
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId.HasValue && plan.AllIds.Contains(activeId.Value))
        {
            await contextStore.ClearActiveWorkItemIdAsync(ct);
        }

        // Process in reverse order (children before parents) to maintain referential integrity
        for (var i = plan.AllIds.Count - 1; i >= 0; i--)
        {
            var id = plan.AllIds[i];
            await seedLinkRepo.DeleteLinksForItemAsync(id, ct);
            await workItemRepo.DeleteByIdAsync(id, ct);
        }
    }
}
