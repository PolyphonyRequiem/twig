using Twig.Domain.Interfaces;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Identifies work items that must not be overwritten during a refresh.
/// Combines dirty items from the repository with items that have pending (uncommitted) changes.
/// </summary>
public static class SyncGuard
{
    /// <summary>
    /// Returns the union of dirty work item IDs (from the repository) and
    /// pending-change item IDs (from the pending change store).
    /// These items must be protected during sync/refresh operations.
    /// </summary>
    public static async Task<IReadOnlySet<int>> GetProtectedItemIdsAsync(
        IWorkItemRepository repo, IPendingChangeStore pendingStore, CancellationToken ct = default)
    {
        var dirtyItems = await repo.GetDirtyItemsAsync(ct);
        var pendingIds = await pendingStore.GetDirtyItemIdsAsync(ct);

        var result = new HashSet<int>();
        foreach (var item in dirtyItems) result.Add(item.Id);
        foreach (var id in pendingIds) result.Add(id);
        return result;
    }
}
