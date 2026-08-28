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
    ITrackingService? trackingService = null,
    IIterationCalendar? iterationCalendar = null,
    IWorkItemLinkRepository? linkRepo = null)
{

    /// <summary>
    /// Refreshes the local iteration calendar from ADO (ADO #144).
    /// <para>
    /// This is the ONLY place the iteration list crosses the network. A Bench's sprint rule is
    /// answered afterwards from this cached mapping plus the local clock, so looking at a Bench
    /// never calls out and works with the endpoint unreachable.
    /// </para>
    /// <para>
    /// Best-effort by design: a calendar refresh failure must not fail the whole refresh, because
    /// the previous mapping still answers the rule correctly until the dates actually move.
    /// </para>
    /// </summary>
    public async Task RefreshIterationCalendarAsync(CancellationToken ct = default)
    {
        if (iterationCalendar is null)
            return;

        try
        {
            var iterations = await iterationService.GetTeamIterationsAsync(ct);
            if (iterations.Count > 0)
                await iterationCalendar.SaveAsync(iterations, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the previous mapping rather than emptying it — an empty calendar would make
            // the sprint rule match nothing and silently empty the person's view.
        }
    }

    /// <summary>
    /// Fetches sprint items, active item, and children from ADO. Returns conflicts if any.
    /// </summary>
    /// <remarks>
    /// Wayfinder 0004 slice 5 removed this method's <c>force</c> parameter. It gated two things
    /// at once: it emptied the protected set, and it switched the save path to raw
    /// <see cref="IWorkItemRepository.SaveBatchAsync"/> calls that walked straight past
    /// <see cref="ProtectedCacheWriter"/>. Emptying the set also silently disabled conflict
    /// detection (<c>FindConflictsAsync</c> returned <c>[]</c> for an empty set), so
    /// <c>--force</c> did not merely overwrite — it suppressed the report of what it overwrote.
    /// There is now one save path and it is the protected one.
    /// </remarks>
    public async Task<RefreshFetchResult> FetchItemsAsync(
        string wiql, CancellationToken ct = default)
    {
        var ids = await adoService.QueryByWiqlAsync(wiql, ct);
        if (ids.Count == 0)
            return new RefreshFetchResult { ItemCount = 0 };

        var realIds = ids.Where(id => id > 0).ToList();

        // Cleanse phantom dirty flags before SyncGuard evaluation (#1335)
        var phantomsCleansed = await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);

        var protectedIds = await SyncGuard.GetProtectedItemIdsAsync(workItemRepo, pendingChangeStore, ct);

        IReadOnlyList<WorkItem> sprintItems = [];
        IReadOnlyList<WorkItemLink> sprintLinks = [];
        WorkItem? activeItem = null;
        IReadOnlyList<WorkItemLink> activeLinks = [];
        IReadOnlyList<WorkItem> childItems = [];
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);

        // AB#831: FetchBatchWithLinksAsync, not FetchBatchAsync. The batch URL has always
        // carried $expand=relations, so the edges were already on the wire on every refresh —
        // FetchBatchAsync simply discarded them, and `twig sync` left work_item_links empty
        // while filling work_items. Keeping them costs no extra round trip.
        if (realIds.Count > 0)
            (sprintItems, sprintLinks) = await adoService.FetchBatchWithLinksAsync(realIds, ct);

        if (activeId.HasValue && activeId.Value > 0)
        {
            var fetchChildrenTask = adoService.FetchChildrenAsync(activeId.Value, ct);

            if (!realIds.Contains(activeId.Value))
            {
                // The active item is the one `twig show` reads without an id, so its edge set is
                // the one most likely to be consulted — fetch it with relations for the same
                // reason as the batch above.
                var fetchActiveTask = adoService.FetchWithLinksAsync(activeId.Value, ct);
                await Task.WhenAll(fetchActiveTask, fetchChildrenTask);
                (activeItem, activeLinks) = fetchActiveTask.Result;
                childItems = fetchChildrenTask.Result;
            }
            else
            {
                childItems = await fetchChildrenTask;
            }
        }

        // Detect revision conflicts
        var conflicts = await FindConflictsAsync(sprintItems, activeItem, childItems, protectedIds, ct);

        // Save. One path, always protected — slice 5 deleted the `force` branch that used raw
        // SaveBatchAsync/SaveAsync to write over items the user had staged edits on.
        if (sprintItems.Count > 0)
            await protectedCacheWriter.SaveBatchProtectedAsync(sprintItems, protectedIds, ct);
        if (activeItem is not null)
            await protectedCacheWriter.SaveProtectedAsync(activeItem, protectedIds, ct);
        if (childItems.Count > 0)
            await protectedCacheWriter.SaveBatchProtectedAsync(childItems, protectedIds, ct);

        // AB#831. Written per SOURCE id AFTER the item rows, for every id that came back —
        // including ids that came back with no edges at all. Writing only the ids that carried
        // links would leave a previously-linked item's stale edges behind and, worse, leave an
        // edgeless item unstamped and therefore indistinguishable from one never fetched, which
        // is the exact ambiguity this ticket exists to remove.
        //
        // Children are deliberately NOT stamped: FetchChildrenAsync does not return relations,
        // so claiming their edge sets were verified would be a fresh lie. They read back as
        // unverified, which is the honest answer.
        await SaveLinksForFetchedAsync(sprintItems, sprintLinks, ct);
        if (activeItem is not null)
            await SaveLinksForFetchedAsync([activeItem], activeLinks, ct);

        return new RefreshFetchResult
        {
            ItemCount = realIds.Count,
            Conflicts = conflicts,
            PhantomsCleansed = phantomsCleansed,
        };
    }

    /// <summary>
    /// Persists the edge set of every item in <paramref name="fetched"/>, bucketed by source id
    /// (AB#831). Best-effort: a link-store failure degrades the refresh to items-without-edges
    /// rather than failing it, matching how every other read path treats the link store.
    /// </summary>
    /// <remarks>
    /// 🔴 Every FETCHED id is written, not every id that appears as a link source. Link
    /// persistence replaces a source's whole edge set, so skipping the edgeless ids would both
    /// leave stale edges behind after a link was removed in ADO and leave those ids unstamped —
    /// and an unstamped id reads back as "never verified", which is exactly the answer this
    /// refresh has just earned the right to stop giving.
    /// <para>
    /// 🔴 Ids that <see cref="ProtectedCacheWriter"/> skipped are written here too, and that is
    /// deliberate. The guard protects an item's <c>work_items</c> ROW — a field or state edit the
    /// user has staged and ADO has not seen. It does not protect edges, because there is no such
    /// thing as a staged local edge: <c>twig link</c> is push-on-write (it calls ADO, then
    /// resyncs) and a seed's edges live in the durable <c>seed_links</c> table. So
    /// <c>work_item_links</c> holds remote truth only, and refreshing it beside a locally-edited
    /// item corrects the edges rather than discarding anyone's work. Skipping protected ids would
    /// also be strictly worse for this ticket: they would go unstamped and read back as "never
    /// verified" — the very answer this refresh exists to stop giving.
    /// </para>
    /// </remarks>
    private async Task SaveLinksForFetchedAsync(
        IReadOnlyList<WorkItem> fetched, IReadOnlyList<WorkItemLink> links, CancellationToken ct)
    {
        if (linkRepo is null || fetched.Count == 0)
            return;

        var bySource = new Dictionary<int, IReadOnlyList<WorkItemLink>>(fetched.Count);

        // Seed EVERY fetched id with an empty set first, then fill. This is what makes an id
        // that came back with no edges still get written and stamped.
        foreach (var item in fetched)
            bySource[item.Id] = Array.Empty<WorkItemLink>();

        foreach (var link in links)
        {
            if (!bySource.TryGetValue(link.SourceId, out var existing))
                continue;   // an edge whose source is not in this fetch is not ours to write

            if (existing is List<WorkItemLink> bucket)
            {
                bucket.Add(link);
            }
            else
            {
                bySource[link.SourceId] = new List<WorkItemLink> { link };
            }
        }

        try
        {
            // ONE transaction for the whole set. Per-item calls cost 370-700 ms across a
            // 163-item refresh, because the cache runs WAL with synchronous=FULL and every
            // commit fsyncs; the same writes batched measure ~3 ms.
            await linkRepo.SaveLinksForSourcesAsync(bySource, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort by design: the item rows are already saved and useful.
        }
    }

    /// <summary>
    /// Iteratively hydrates orphan parent IDs (up to 5 levels).
    /// </summary>
    /// <remarks>
    /// Wayfinder 0004 slice 5: this used a raw <see cref="IWorkItemRepository.SaveBatchAsync"/>
    /// and was <b>not</b> behind <c>force</c>, so it overwrote staged local edits on ancestors on
    /// every refresh — the default path included, with no flag to opt into it. It now writes
    /// through <see cref="ProtectedCacheWriter"/> like every other refresh write.
    /// <para>
    /// The loop termination must key off what was <i>fetched</i>, not what was written: a level
    /// whose ancestors are all protected still resolves those orphan parents for the next
    /// iteration's <c>GetOrphanParentIdsAsync</c>, and breaking on an empty write would leave the
    /// hierarchy half-hydrated whenever the user happened to have an ancestor staged.
    /// </para>
    /// <para>
    /// <b>But a fully-protected level makes no progress.</b> <c>GetOrphanParentIdsAsync</c> finds
    /// parent ids with no <c>work_items</c> row; a protected ancestor is never written, so it stays
    /// an orphan and the next iteration returns the same id — re-fetching it from ADO each time
    /// until the 5-level cap stops the loop. Correctness is unaffected (the guard is doing its
    /// job), but the cap would be silently absorbing up to four redundant round-trips. Tracking
    /// the ids already seen ends the walk as soon as a level adds nothing new, which is the honest
    /// termination condition: progress means <i>new</i> ancestors, not written ones.
    /// </para>
    /// </remarks>
    public async Task HydrateAncestorsAsync(CancellationToken ct = default)
    {
        var seen = new HashSet<int>();

        for (var level = 0; level < 5; level++)
        {
            var orphanIds = await workItemRepo.GetOrphanParentIdsAsync(ct);
            if (orphanIds.Count == 0) break;

            // A level that surfaces no id we have not already fetched cannot make progress —
            // every remaining orphan is one a protected write deliberately left in place.
            var unseen = orphanIds.Where(seen.Add).ToList();
            if (unseen.Count == 0) break;

            var ancestors = await adoService.FetchBatchAsync(unseen, ct);
            if (ancestors.Count == 0) break;

            await protectedCacheWriter.SaveBatchProtectedAsync(ancestors, ct);
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
