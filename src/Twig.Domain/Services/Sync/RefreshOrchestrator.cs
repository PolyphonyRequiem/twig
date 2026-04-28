using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Sync;

/// <summary>
/// Manages the full refresh lifecycle: WIQL sprint fetch, conflict resolution, and ancestor hydration.
/// 9 dependencies, 1 consumer (<see cref="RefreshCommand"/>).
/// Retained as a separate orchestrator — substantial business logic with a single, well-defined responsibility.
/// </summary>
public sealed class RefreshOrchestrator(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    ProtectedCacheWriter protectedCacheWriter,
    WorkingSetService workingSetService,
    SyncCoordinatorFactory syncCoordinatorFactory,
    IIterationService iterationService,
    ITrackingService? trackingService = null)
{

    /// <summary>
    /// Fetches sprint items, active item, and children from ADO. Returns conflicts if any.
    /// </summary>
    public async Task<RefreshFetchResult> FetchItemsAsync(
        string wiql, bool force, CancellationToken ct = default)
    {
        var ids = await adoService.QueryByWiqlAsync(wiql, ct);
        if (ids.Count == 0)
            return new RefreshFetchResult { ItemCount = 0 };

        var realIds = ids.Where(id => id > 0).ToList();

        // Cleanse phantom dirty flags before SyncGuard evaluation (#1335)
        var phantomsCleansed = await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);

        var protectedIds = !force
            ? await SyncGuard.GetProtectedItemIdsAsync(workItemRepo, pendingChangeStore, ct)
            : (IReadOnlySet<int>)new HashSet<int>();

        IReadOnlyList<WorkItem> sprintItems = [];
        WorkItem? activeItem = null;
        IReadOnlyList<WorkItem> childItems = [];
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);

        if (realIds.Count > 0)
            sprintItems = await adoService.FetchBatchAsync(realIds, ct);

        if (activeId.HasValue && activeId.Value > 0)
        {
            var fetchChildrenTask = adoService.FetchChildrenAsync(activeId.Value, ct);

            if (!realIds.Contains(activeId.Value))
            {
                var fetchActiveTask = adoService.FetchAsync(activeId.Value, ct);
                await Task.WhenAll(fetchActiveTask, fetchChildrenTask);
                activeItem = fetchActiveTask.Result;
                childItems = fetchChildrenTask.Result;
            }
            else
            {
                childItems = await fetchChildrenTask;
            }
        }

        // Detect revision conflicts
        var conflicts = await FindConflictsAsync(sprintItems, activeItem, childItems, protectedIds, ct);

        // Save
        if (force)
        {
            if (sprintItems.Count > 0)
                await workItemRepo.SaveBatchAsync(sprintItems, ct);
            if (activeItem is not null)
                await workItemRepo.SaveAsync(activeItem, ct);
            if (childItems.Count > 0)
                await workItemRepo.SaveBatchAsync(childItems, ct);
        }
        else
        {
            if (sprintItems.Count > 0)
                await protectedCacheWriter.SaveBatchProtectedAsync(sprintItems, protectedIds, ct);
            if (activeItem is not null)
                await protectedCacheWriter.SaveProtectedAsync(activeItem, protectedIds, ct);
            if (childItems.Count > 0)
                await protectedCacheWriter.SaveBatchProtectedAsync(childItems, protectedIds, ct);
        }

        return new RefreshFetchResult
        {
            ItemCount = realIds.Count,
            Conflicts = conflicts,
            PhantomsCleansed = phantomsCleansed,
        };
    }

    /// <summary>
    /// Iteratively hydrates orphan parent IDs (up to 5 levels).
    /// </summary>
    public async Task HydrateAncestorsAsync(CancellationToken ct = default)
    {
        for (var level = 0; level < 5; level++)
        {
            var orphanIds = await workItemRepo.GetOrphanParentIdsAsync(ct);
            if (orphanIds.Count == 0) break;

            var ancestors = await adoService.FetchBatchAsync(orphanIds, ct);
            if (ancestors.Count == 0) break;

            await workItemRepo.SaveBatchAsync(ancestors, ct);
        }
    }

    /// <summary>
    /// Syncs tracked trees by re-exploring each tree-mode root via the ADO API.
    /// Returns the number of items auto-untracked (deleted in ADO), or 0 if tracking is not configured.
    /// </summary>
    public async Task<int> SyncTrackedTreesAsync(CancellationToken ct = default)
    {
        if (trackingService is null)
            return 0;

        return await trackingService.SyncTrackedTreesAsync(syncCoordinatorFactory.ReadWrite, ct);
    }

    /// <summary>
    /// Applies the configured cleanup policy to tracked items.
    /// Resolves the current iteration via <see cref="IIterationService"/> and delegates
    /// to <see cref="ITrackingService.ApplyCleanupPolicyAsync"/>.
    /// Returns the number of items removed, or 0 if tracking is not configured or policy is <see cref="TrackingCleanupPolicy.None"/>.
    /// </summary>
    public async Task<int> ApplyCleanupPolicyAsync(TrackingCleanupPolicy policy, CancellationToken ct = default)
    {
        if (trackingService is null || policy == TrackingCleanupPolicy.None)
            return 0;

        var currentIteration = await iterationService.GetCurrentIterationAsync(ct);
        return await trackingService.ApplyCleanupPolicyAsync(policy, currentIteration, ct);
    }

    /// <summary>Syncs the working set after refresh (no eviction per FR-013).</summary>
    public async Task SyncWorkingSetAsync(IterationPath iteration, CancellationToken ct = default)
    {
        var workingSet = await workingSetService.ComputeAsync([iteration], ct);
        await syncCoordinatorFactory.ReadWrite.SyncWorkingSetAsync(workingSet, ct);
    }

    private async Task<IReadOnlyList<RefreshConflict>> FindConflictsAsync(
        IReadOnlyList<WorkItem> sprintItems, WorkItem? activeItem,
        IReadOnlyList<WorkItem> childItems, IReadOnlySet<int> protectedIds,
        CancellationToken ct)
    {
        if (protectedIds.Count == 0)
            return [];

        var conflicts = new List<RefreshConflict>();

        async Task CheckItems(IEnumerable<WorkItem> items)
        {
            foreach (var remoteItem in items)
            {
                if (!protectedIds.Contains(remoteItem.Id)) continue;
                var localItem = await workItemRepo.GetByIdAsync(remoteItem.Id, ct);
                if (localItem is not null && remoteItem.Revision > localItem.Revision)
                    conflicts.Add(new RefreshConflict(remoteItem.Id, localItem.Revision, remoteItem.Revision));
            }
        }

        await CheckItems(sprintItems);
        if (activeItem is not null)
            await CheckItems([activeItem]);
        await CheckItems(childItems);

        return conflicts;
    }
}

/// <summary>Result of a refresh fetch operation.</summary>
public sealed class RefreshFetchResult
{
    public int ItemCount { get; init; }
    public int PhantomsCleansed { get; init; }
    public IReadOnlyList<RefreshConflict> Conflicts { get; init; } = [];
}

/// <summary>A revision conflict detected during refresh.</summary>
public sealed record RefreshConflict(int Id, int LocalRevision, int RemoteRevision);
