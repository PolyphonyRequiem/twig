using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

/// <summary>
/// Builds a cascade-discard plan and executes cascade deletion of seeds.
/// Consumed by <c>SeedDiscardCommand</c>.
/// </summary>
public sealed class SeedDiscardOrchestrator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IContextStore _contextStore;
    private readonly IPendingChangeStore _pendingChangeStore;

    /// <summary>
    /// Creates an orchestrator that clears staged pending changes before deleting each
    /// seed row, so a seed carrying a staged note or field edit can be discarded.
    /// </summary>
    /// <remarks>
    /// <see cref="IPendingChangeStore"/> is required, not optional (wayfinder 0004 §4).
    /// The former store-less overload made correctness depend on every construction site
    /// picking the right constructor: omitting the store left staged edits orphaned in
    /// <c>pending.db</c> on discard (PolyphonyRequiem/twig#268).
    /// A dependency correctness depends on is not optional.
    /// </remarks>
    public SeedDiscardOrchestrator(
        IWorkItemRepository workItemRepo,
        ISeedLinkRepository seedLinkRepo,
        IContextStore contextStore,
        IPendingChangeStore pendingChangeStore)
    {
        _workItemRepo = workItemRepo;
        _seedLinkRepo = seedLinkRepo;
        _contextStore = contextStore;
        _pendingChangeStore = pendingChangeStore;
    }

    /// <summary>
    /// Validates the target seed exists and is a seed, then performs a BFS traversal
    /// of the seed graph to collect all descendant seed IDs.
    /// Returns <c>null</c> if the seed is not found or the item is not a seed.
    /// </summary>
    public async Task<SeedDiscardPlan?> BuildDiscardPlanAsync(int seedId, CancellationToken ct = default)
    {
        var target = await _workItemRepo.GetByIdAsync(seedId, ct);
        if (target is null || !target.IsSeed)
            return null;

        var allSeeds = await _workItemRepo.GetSeedsAsync(ct);

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
    /// Executes the cascade discard: clears active context if needed, clears pending
    /// changes, deletes seed links, then deletes work item rows. Processes children
    /// before parents (reverse BFS order) to maintain referential integrity.
    /// </summary>
    public async Task ExecuteDiscardAsync(SeedDiscardPlan plan, CancellationToken ct = default)
    {
        // Clear active context if the current work item is any of the IDs being discarded
        var activeId = await _contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId.HasValue && plan.AllIds.Contains(activeId.Value))
        {
            await _contextStore.ClearActiveWorkItemIdAsync(ct);
        }

        // Process in reverse order (children before parents) to maintain referential integrity
        for (var i = plan.AllIds.Count - 1; i >= 0; i--)
        {
            var id = plan.AllIds[i];

            // #268 was a FOREIGN KEY from pending_changes to work_items(id): a staged note kept
            // a live reference, so the row delete below raised a constraint violation that the
            // CLI surfaced as the highly misleading "Cache corrupted. Run 'twig init --force'".
            // Wayfinder 0013 deleted that FK by moving pending_changes to the durable store, so
            // the crash is now unexpressible. Clearing first is retained deliberately: discard
            // means the staged edits go too, and leaving them would orphan them in pending.db.
            await _pendingChangeStore.ClearChangesAsync(id, ct);
            await _seedLinkRepo.DeleteLinksForItemAsync(id, ct);
            await _workItemRepo.DeleteByIdAsync(id, ct);
        }
    }
}
