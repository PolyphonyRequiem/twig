using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services;

/// <summary>
/// Writes work items to the cache while protecting dirty/pending items from overwrite.
/// Delegates to <see cref="SyncGuard"/> for protected ID resolution.
/// </summary>
public sealed class ProtectedCacheWriter
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;

    public ProtectedCacheWriter(
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingChangeStore)
    {
        _workItemRepo = workItemRepo;
        _pendingChangeStore = pendingChangeStore;
    }

    /// <summary>
    /// Saves a batch of work items, skipping any that are protected (dirty or have pending changes).
    /// Returns the list of IDs that were skipped.
    /// </summary>
    public async Task<IReadOnlyList<int>> SaveBatchProtectedAsync(
        IEnumerable<WorkItem> items, CancellationToken ct = default)
    {
        var protectedIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingChangeStore, ct);

        var toSave = new List<WorkItem>();
        var skippedIds = new List<int>();

        foreach (var item in items)
        {
            if (protectedIds.Contains(item.Id))
                skippedIds.Add(item.Id);
            else
                toSave.Add(item);
        }

        if (toSave.Count > 0)
            await _workItemRepo.SaveBatchAsync(toSave, ct);

        return skippedIds;
    }

    /// <summary>
    /// Saves a single work item if it is not protected.
    /// Returns <c>true</c> if saved, <c>false</c> if skipped.
    /// </summary>
    public async Task<bool> SaveProtectedAsync(WorkItem item, CancellationToken ct = default)
    {
        var protectedIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingChangeStore, ct);

        if (protectedIds.Contains(item.Id))
            return false;

        await _workItemRepo.SaveAsync(item, ct);
        return true;
    }
}
