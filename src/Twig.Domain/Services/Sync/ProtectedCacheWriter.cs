using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Writes work items to the cache while protecting dirty/pending items from overwrite.
/// Delegates to <see cref="SyncGuard"/> for protected ID resolution.
/// </summary>
/// <remarks>
/// The batch overloads return the skipped <see cref="WorkItem"/>s themselves, not their IDs.
/// Per wayfinder 0004 §4 a skipped item is precisely the case where local and remote have
/// both moved, so the freshly fetched remote snapshot is the input reconciliation needs to
/// hand <see cref="ConflictResolver"/>. Returning only IDs discarded it, and every caller
/// then reduced even that to a count — the remote side was fetched, examined, and thrown
/// away in the same statement.
/// </remarks>
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
    /// Returns the skipped items — the remote snapshots that were fetched but not written,
    /// which are the reconciliation inputs for the items whose local state diverged.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> SaveBatchProtectedAsync(
        IEnumerable<WorkItem> items, CancellationToken ct = default)
    {
        var protectedIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingChangeStore, ct);
        return await SaveBatchProtectedAsync(items, protectedIds, ct);
    }

    /// <summary>
    /// Saves a batch of work items using pre-computed protected IDs, skipping any that are protected.
    /// Avoids redundant <see cref="SyncGuard"/> queries when the caller has already computed protected IDs.
    /// Returns the skipped items — see the remarks on <see cref="ProtectedCacheWriter"/>.
    /// </summary>
    public async Task<IReadOnlyList<WorkItem>> SaveBatchProtectedAsync(
        IEnumerable<WorkItem> items, IReadOnlySet<int> protectedIds, CancellationToken ct = default)
    {
        var toSave = new List<WorkItem>();
        var skipped = new List<WorkItem>();

        foreach (var item in items)
        {
            if (protectedIds.Contains(item.Id))
                skipped.Add(item);
            else
                toSave.Add(item);
        }

        if (toSave.Count > 0)
            await _workItemRepo.SaveBatchAsync(toSave, ct);

        return skipped;
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

    /// <summary>
    /// Saves a single work item using pre-computed protected IDs.
    /// Avoids redundant <see cref="SyncGuard"/> queries when the caller has already computed protected IDs.
    /// Returns <c>true</c> if saved, <c>false</c> if skipped.
    /// </summary>
    public async Task<bool> SaveProtectedAsync(WorkItem item, IReadOnlySet<int> protectedIds, CancellationToken ct = default)
    {
        if (protectedIds.Contains(item.Id))
            return false;

        await _workItemRepo.SaveAsync(item, ct);
        return true;
    }
}
