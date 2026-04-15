using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Encapsulates the core refresh logic: ADO fetch → conflict detection → batch save →
/// ancestor hydration → working set sync.
/// <c>RefreshCommand</c> builds the WIQL string (which depends on Infrastructure config)
/// and delegates the rest to this service.
/// </summary>
public sealed class RefreshOrchestrator
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IIterationService _iterationService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;

    public RefreshOrchestrator(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IIterationService iterationService,
        IPendingChangeStore pendingChangeStore,
        ProtectedCacheWriter protectedCacheWriter,
        WorkingSetService workingSetService,
        SyncCoordinator syncCoordinator,
        IProcessTypeStore processTypeStore,
        IFieldDefinitionStore fieldDefinitionStore)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _adoService = adoService;
        _iterationService = iterationService;
        _pendingChangeStore = pendingChangeStore;
        _protectedCacheWriter = protectedCacheWriter;
        _workingSetService = workingSetService;
        _syncCoordinator = syncCoordinator;
        _processTypeStore = processTypeStore;
        _fieldDefinitionStore = fieldDefinitionStore;
    }

    /// <summary>
    /// Fetches sprint items, active item, and children from ADO. Returns conflicts if any.
    /// </summary>
    public async Task<RefreshFetchResult> FetchItemsAsync(
        string wiql, bool force, CancellationToken ct = default)
    {
        var ids = await _adoService.QueryByWiqlAsync(wiql, ct);
        if (ids.Count == 0)
            return new RefreshFetchResult { ItemCount = 0 };

        var realIds = ids.Where(id => id > 0).ToList();

        // Cleanse phantom dirty flags before SyncGuard evaluation (#1335)
        var phantomsCleansed = await _workItemRepo.ClearPhantomDirtyFlagsAsync(ct);

        var protectedIds = !force
            ? await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingChangeStore, ct)
            : (IReadOnlySet<int>)new HashSet<int>();

        IReadOnlyList<WorkItem> sprintItems = [];
        WorkItem? activeItem = null;
        IReadOnlyList<WorkItem> childItems = [];
        var activeId = await _contextStore.GetActiveWorkItemIdAsync(ct);

        if (realIds.Count > 0)
            sprintItems = await _adoService.FetchBatchAsync(realIds, ct);

        if (activeId.HasValue && activeId.Value > 0)
        {
            var fetchChildrenTask = _adoService.FetchChildrenAsync(activeId.Value, ct);

            if (!realIds.Contains(activeId.Value))
            {
                var fetchActiveTask = _adoService.FetchAsync(activeId.Value, ct);
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
                await _workItemRepo.SaveBatchAsync(sprintItems, ct);
            if (activeItem is not null)
                await _workItemRepo.SaveAsync(activeItem, ct);
            if (childItems.Count > 0)
                await _workItemRepo.SaveBatchAsync(childItems, ct);
        }
        else
        {
            if (sprintItems.Count > 0)
                await _protectedCacheWriter.SaveBatchProtectedAsync(sprintItems, protectedIds, ct);
            if (activeItem is not null)
                await _protectedCacheWriter.SaveProtectedAsync(activeItem, protectedIds, ct);
            if (childItems.Count > 0)
                await _protectedCacheWriter.SaveBatchProtectedAsync(childItems, protectedIds, ct);
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
            var orphanIds = await _workItemRepo.GetOrphanParentIdsAsync(ct);
            if (orphanIds.Count == 0) break;

            var ancestors = await _adoService.FetchBatchAsync(orphanIds, ct);
            if (ancestors.Count == 0) break;

            await _workItemRepo.SaveBatchAsync(ancestors, ct);
        }
    }

    /// <summary>Syncs the working set after refresh (no eviction per FR-013).</summary>
    public async Task SyncWorkingSetAsync(IterationPath iteration, CancellationToken ct = default)
    {
        var workingSet = await _workingSetService.ComputeAsync(iteration, ct);
        await _syncCoordinator.SyncWorkingSetAsync(workingSet, ct);
    }

    /// <summary>Syncs process types from ADO.</summary>
    public async Task SyncProcessTypesAsync(CancellationToken ct = default)
    {
        await ProcessTypeSyncService.SyncAsync(_iterationService, _processTypeStore, ct);
    }

    /// <summary>Syncs field definitions from ADO.</summary>
    public async Task SyncFieldDefinitionsAsync(CancellationToken ct = default)
    {
        await FieldDefinitionSyncService.SyncAsync(_iterationService, _fieldDefinitionStore, ct);
    }

    /// <summary>Gets the current iteration path.</summary>
    public Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default) =>
        _iterationService.GetCurrentIterationAsync(ct);

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
                var localItem = await _workItemRepo.GetByIdAsync(remoteItem.Id, ct);
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
