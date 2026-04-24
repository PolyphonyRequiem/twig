using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Domain service that orchestrates tracking and exclusion operations
/// by delegating to <see cref="ITrackingRepository"/>.
/// </summary>
public sealed class TrackingService(ITrackingRepository repository) : ITrackingService
{
    /// <inheritdoc />
    public Task TrackAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
        => repository.UpsertTrackedAsync(workItemId, mode, ct);

    /// <inheritdoc />
    public Task TrackTreeAsync(int workItemId, CancellationToken ct = default)
        => TrackAsync(workItemId, TrackingMode.Tree, ct);

    /// <inheritdoc />
    public async Task<bool> UntrackAsync(int workItemId, CancellationToken ct = default)
    {
        var existing = await repository.GetTrackedByWorkItemIdAsync(workItemId, ct);
        if (existing is null)
            return false;

        await repository.RemoveTrackedAsync(workItemId, ct);
        return true;
    }

    /// <inheritdoc />
    public Task ExcludeAsync(int workItemId, CancellationToken ct = default)
        => repository.AddExcludedAsync(workItemId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default)
        => repository.GetAllTrackedAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetExcludedIdsAsync(CancellationToken ct = default)
    {
        var excluded = await repository.GetAllExcludedAsync(ct);
        return excluded.Select(e => e.WorkItemId).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExcludedItem>> ListExclusionsAsync(CancellationToken ct = default)
        => repository.GetAllExcludedAsync(ct);

    /// <inheritdoc />
    public async Task<bool> RemoveExclusionAsync(int workItemId, CancellationToken ct = default)
    {
        var existing = await repository.GetAllExcludedAsync(ct);
        if (!existing.Any(e => e.WorkItemId == workItemId))
            return false;

        await repository.RemoveExcludedAsync(workItemId, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ClearExclusionsAsync(CancellationToken ct = default)
    {
        var existing = await repository.GetAllExcludedAsync(ct);
        var count = existing.Count;
        if (count > 0)
            await repository.ClearAllExcludedAsync(ct);

        return count;
    }

    /// <inheritdoc />
    public async Task<int> SyncTrackedTreesAsync(SyncCoordinator syncCoordinator, CancellationToken ct = default)
    {
        var tracked = await repository.GetAllTrackedAsync(ct);
        var treeItems = tracked.Where(t => t.Mode == TrackingMode.Tree).ToList();

        if (treeItems.Count == 0)
            return 0;

        var untrackedIds = new List<int>();

        foreach (var item in treeItems)
        {
            var rootResult = await syncCoordinator.SyncItemAsync(item.WorkItemId, ct);

            if (rootResult is SyncResult.Failed { Reason: var reason } &&
                reason.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                untrackedIds.Add(item.WorkItemId);
                continue;
            }

            await syncCoordinator.SyncChildrenAsync(item.WorkItemId, ct);
        }

        if (untrackedIds.Count > 0)
            await repository.RemoveTrackedBatchAsync(untrackedIds, ct);

        return untrackedIds.Count;
    }
}
