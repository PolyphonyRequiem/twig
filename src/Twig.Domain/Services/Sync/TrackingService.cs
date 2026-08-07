using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Domain service that orchestrates tracking and exclusion operations
/// by delegating to <see cref="ITrackingRepository"/>.
/// </summary>
public sealed class TrackingService(
    ITrackingRepository repository,
    IWorkItemRepository workItemRepository,
    IProcessTypeStore processTypeStore,
    IPinReader? pinReader = null,
    IPinWriter? pinWriter = null) : ITrackingService
{
    /// <summary>
    /// Pins come from the BENCH, not the tracking file (ADO #146).
    /// <para>
    /// 🔴 The file is no longer a pin store. Falling back to it when no reader is wired up would
    /// resurrect the exact dual-source this ticket removed, and the symptom — a tracked tree that
    /// quietly stops refreshing — is invisible to the parity baseline. With no reader there are
    /// no pins, which is honest rather than stale.
    /// </para>
    /// </summary>
    private Task<IReadOnlyList<TrackedItem>> ReadPinsAsync(CancellationToken ct)
        => pinReader is not null
            ? pinReader.GetPinsAsync(ct)
            : Task.FromResult<IReadOnlyList<TrackedItem>>([]);

    /// <inheritdoc />
    public Task TrackAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
        => pinWriter is not null
            ? pinWriter.AddPinAsync(workItemId, mode, ct)
            : Task.CompletedTask;

    /// <inheritdoc />
    public Task TrackTreeAsync(int workItemId, CancellationToken ct = default)
        => TrackAsync(workItemId, TrackingMode.Tree, ct);

    /// <inheritdoc />
    public async Task<bool> UntrackAsync(int workItemId, CancellationToken ct = default)
    {
        if (pinWriter is null)
            return false;

        return await pinWriter.RemovePinAsync(workItemId, ct);
    }

    /// <inheritdoc />
    public Task ExcludeAsync(int workItemId, CancellationToken ct = default)
        => repository.AddExcludedAsync(workItemId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default)
        => ReadPinsAsync(ct);

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
        var tracked = await ReadPinsAsync(ct);
        var treeItems = tracked.Where(t => t.Mode == TrackingMode.Tree).ToList();

        if (treeItems.Count == 0)
            return 0;

        var untrackedIds = new List<int>();

        foreach (var item in treeItems)
        {
            var rootResult = await syncCoordinator.SyncItemAsync(item.WorkItemId, ct);

            if (rootResult is SyncFailed { Reason: var reason } &&
                reason.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                untrackedIds.Add(item.WorkItemId);
                continue;
            }

            await syncCoordinator.SyncParentChainAsync(item.WorkItemId, ct);
            await syncCoordinator.SyncChildrenAsync(item.WorkItemId, ct);
            await syncCoordinator.SyncRootLinksAsync(item.WorkItemId, ct);
        }

        if (untrackedIds.Count > 0 && pinWriter is not null)
        {
            foreach (var id in untrackedIds)
                await pinWriter.RemovePinAsync(id, ct);
        }

        return untrackedIds.Count;
    }

    /// <inheritdoc />
    public async Task<int> ApplyCleanupPolicyAsync(
        TrackingCleanupPolicy policy,
        IterationPath currentIteration,
        CancellationToken ct = default)
    {
        if (policy == TrackingCleanupPolicy.None)
            return 0;

        var tracked = await ReadPinsAsync(ct);
        if (tracked.Count == 0)
            return 0;

        var workItemIds = tracked.Select(t => t.WorkItemId).ToList();
        var workItems = await workItemRepository.GetByIdsAsync(workItemIds, ct);
        var workItemLookup = workItems.ToDictionary(w => w.Id);

        var removalIds = new List<int>();
        var processTypeCache = new Dictionary<string, ProcessTypeRecord?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in tracked)
        {
            if (!workItemLookup.TryGetValue(item.WorkItemId, out var workItem))
                continue;

            if (!processTypeCache.TryGetValue(workItem.Type.Value, out var processType))
            {
                processType = await processTypeStore.GetByNameAsync(workItem.Type.Value, ct);
                processTypeCache[workItem.Type.Value] = processType;
            }

            var category = StateCategoryResolver.Resolve(workItem.State, processType?.States);

            var isCompleted = category == StateCategory.Completed;

            switch (policy)
            {
                case TrackingCleanupPolicy.OnComplete when isCompleted:
                    removalIds.Add(item.WorkItemId);
                    break;

                // IterationPath has no total ordering; != approximates "past" for the common
                // case where items aren't pre-assigned to future sprints. A completed item in
                // a future iteration will also be removed — see the behavioral test
                // ApplyCleanupPolicyAsync_OnCompleteAndPast_RemovesCompletedInFutureIteration.
                case TrackingCleanupPolicy.OnCompleteAndPast
                    when isCompleted && workItem.IterationPath != currentIteration:
                    removalIds.Add(item.WorkItemId);
                    break;
            }
        }

        if (removalIds.Count > 0)
        {
            if (pinWriter is not null)
            {
                foreach (var id in removalIds)
                    await pinWriter.RemovePinAsync(id, ct);
            }
        }

        return removalIds.Count;
    }
}
