using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Coordinates sync operations between the local cache and Azure DevOps.
/// Uses per-item <c>LastSyncedAt</c> for staleness checks (DD-8)
/// and accepts <c>int cacheStaleMinutes</c> to avoid Domain→Infrastructure dependency (DD-13).
/// </summary>
public sealed class SyncCoordinator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IWorkItemLinkRepository? _linkRepo;
    private readonly int _cacheStaleMinutes;

    public SyncCoordinator(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        IWorkItemLinkRepository? linkRepo,
        int cacheStaleMinutes)
    {
        _workItemRepo = workItemRepo;
        _adoService = adoService;
        _protectedCacheWriter = protectedCacheWriter;
        _pendingChangeStore = pendingChangeStore;
        _linkRepo = linkRepo;
        _cacheStaleMinutes = cacheStaleMinutes;
    }

    public SyncCoordinator(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        ProtectedCacheWriter protectedCacheWriter,
        IPendingChangeStore pendingChangeStore,
        int cacheStaleMinutes)
        : this(workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, null, cacheStaleMinutes) { }

    /// <summary>
    /// Syncs a single item by ID. Returns <see cref="SyncResult.UpToDate"/> if the item
    /// was recently synced (within <c>cacheStaleMinutes</c>), otherwise fetches from ADO
    /// and saves through the protected cache writer.
    /// </summary>
    public async Task<SyncResult> SyncItemAsync(int id, CancellationToken ct = default)
    {
        var existing = await _workItemRepo.GetByIdAsync(id, ct);

        if (existing?.LastSyncedAt is not null &&
            DateTimeOffset.UtcNow - existing.LastSyncedAt.Value < TimeSpan.FromMinutes(_cacheStaleMinutes))
        {
            return new SyncResult.UpToDate();
        }

        try
        {
            var fetched = await _adoService.FetchAsync(id, ct);
            var saved = await _protectedCacheWriter.SaveProtectedAsync(fetched, ct);
            return saved
                ? new SyncResult.Updated(1)
                : new SyncResult.Skipped("Item has local pending changes");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                await _pendingChangeStore.ClearChangesAsync(id, ct);
                await _workItemRepo.DeleteByIdAsync(id, ct);
            }
            return new SyncResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Syncs stale items within the working set. Skips seed IDs (negative) and fresh items.
    /// Fetches stale items concurrently, then saves the batch through
    /// <see cref="ProtectedCacheWriter.SaveBatchProtectedAsync(IEnumerable{WorkItem}, CancellationToken)"/>
    /// which computes protected IDs once internally (NFR-003, avoids N+1 SyncGuard queries).
    /// </summary>
    public async Task<SyncResult> SyncWorkingSetAsync(
        WorkingSet workingSet, CancellationToken ct = default)
    {
        try
        {
            var candidateIds = workingSet.AllIds
                .Where(id => id > 0 && !workingSet.DirtyItemIds.Contains(id))
                .ToList();
            if (candidateIds.Count == 0) return new SyncResult.UpToDate();
            return await FetchStaleAndSaveAsync(candidateIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SyncResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Syncs a specific set of item IDs. Filters out negative IDs (seeds), checks per-item
    /// staleness, fetches stale items concurrently, and saves through the protected cache writer.
    /// Unlike <see cref="SyncWorkingSetAsync"/>, accepts explicit IDs without requiring a
    /// <see cref="WorkingSet"/> — dirty protection is enforced at write time by
    /// <see cref="ProtectedCacheWriter.SaveBatchProtectedAsync(IEnumerable{WorkItem}, CancellationToken)"/>.
    /// </summary>
    public async Task<SyncResult> SyncItemSetAsync(
        IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        try
        {
            var candidateIds = ids.Where(id => id > 0).ToList();
            if (candidateIds.Count == 0) return new SyncResult.UpToDate();
            return await FetchStaleAndSaveAsync(candidateIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SyncResult.Failed(ex.Message);
        }
    }

    private async Task<SyncResult> FetchStaleAndSaveAsync(List<int> candidateIds, CancellationToken ct)
    {
        var threshold = TimeSpan.FromMinutes(_cacheStaleMinutes);
        var staleIds = new List<int>();
        foreach (var id in candidateIds)
        {
            var existing = await _workItemRepo.GetByIdAsync(id, ct);
            if (existing?.LastSyncedAt is null || DateTimeOffset.UtcNow - existing.LastSyncedAt.Value >= threshold)
                staleIds.Add(id);
        }

        if (staleIds.Count == 0) return new SyncResult.UpToDate();

        var fetchResults = await Task.WhenAll(staleIds.Select(async id =>
        {
            try
            {
                var item = await _adoService.FetchAsync(id, ct);
                return (Id: id, Item: item, Error: (Exception?)null, NotFound: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Detect "not found" errors (deleted/destroyed items) by convention:
                // Infrastructure throws AdoNotFoundException with message "Work item {id} not found."
                var notFound = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
                return (Id: id, Item: (WorkItem?)null, Error: ex, NotFound: notFound);
            }
        }));

        // Evict items confirmed deleted in ADO — prevents stale cache ghosts
        var notFoundIds = fetchResults.Where(r => r.NotFound).Select(r => r.Id).ToList();
        foreach (var id in notFoundIds)
        {
            await _pendingChangeStore.ClearChangesAsync(id, ct);
            await _workItemRepo.DeleteByIdAsync(id, ct);
        }

        var fetchedItems = fetchResults.Where(r => r.Item is not null).Select(r => r.Item!).ToArray();
        var fetchFailures = fetchResults.Where(r => r.Error is not null && !r.NotFound)
            .Select(r => new SyncItemFailure(r.Id, r.Error!.Message))
            .ToList();

        var skippedIds = await _protectedCacheWriter.SaveBatchProtectedAsync(fetchedItems, ct);
        var savedCount = fetchedItems.Length - skippedIds.Count;

        if (fetchFailures.Count > 0 && fetchedItems.Length > 0)
            return new SyncResult.PartiallyUpdated(savedCount, fetchFailures);

        if (fetchFailures.Count > 0)
            return new SyncResult.Failed(string.Join("; ", fetchFailures.Select(f => $"#{f.Id}: {f.Error}")));

        return new SyncResult.Updated(savedCount);
    }

    /// <summary>
    /// Syncs all children of a parent item. Always fetches unconditionally (DD-15) —
    /// no per-parent staleness check. Returns counts of updated vs skipped items.
    /// </summary>
    public async Task<SyncResult> SyncChildrenAsync(int parentId, CancellationToken ct = default)
    {
        try
        {
            var children = await _adoService.FetchChildrenAsync(parentId, ct);
            var skippedIds = await _protectedCacheWriter.SaveBatchProtectedAsync(children, ct);
            var savedCount = children.Count - skippedIds.Count;
            return new SyncResult.Updated(savedCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SyncResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Fetches a work item with its non-hierarchy links from ADO, persists both,
    /// and returns the links. Requires <see cref="IWorkItemLinkRepository"/> to be
    /// provided via the 5-parameter constructor.
    /// </summary>
    public async Task<IReadOnlyList<WorkItemLink>> SyncLinksAsync(int itemId, CancellationToken ct = default)
    {
        var (fetched, links) = await _adoService.FetchWithLinksAsync(itemId, ct);
        await _protectedCacheWriter.SaveProtectedAsync(fetched, ct);
        if (_linkRepo is not null)
            await _linkRepo.SaveLinksAsync(itemId, links, ct);
        return links;
    }
}
