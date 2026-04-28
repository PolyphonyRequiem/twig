using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Sync;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Computes the working set from local cache state.
/// Most queries are local SQLite; the one exception is
/// <see cref="IIterationService.GetCurrentIterationAsync"/> (ADO REST),
/// which callers can bypass by providing an <see cref="IterationPath"/> directly.
/// Follows the same primitive-injection pattern as <see cref="SyncCoordinator"/>.
/// </summary>
public sealed class WorkingSetService
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingStore;
    private readonly IIterationService _iterationService;
    private readonly ITrackingRepository? _trackingRepo;
    private readonly string? _userDisplayName;

    public WorkingSetService(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingStore,
        IIterationService iterationService,
        string? userDisplayName,
        ITrackingRepository? trackingRepo = null)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _pendingStore = pendingStore;
        _iterationService = iterationService;
        _userDisplayName = userDisplayName;
        _trackingRepo = trackingRepo;
    }

    /// <summary>
    /// Computes the current working set from cache state.
    /// When <paramref name="iterationPaths"/> is provided it is used directly (no ADO call);
    /// otherwise <see cref="IIterationService.GetCurrentIterationAsync"/> is called to get a single iteration.
    /// </summary>
    public async Task<WorkingSet> ComputeAsync(
        IReadOnlyList<IterationPath>? iterationPaths = null, CancellationToken ct = default)
    {
        // 1. Read active ID from context
        var activeId = await _contextStore.GetActiveWorkItemIdAsync(ct);

        // 2. Query parent chain (empty list when no active item or item not in cache)
        var parentChain = activeId.HasValue
            ? await _workItemRepo.GetParentChainAsync(activeId.Value, ct)
            : [];

        // 3. Query children of the active item
        var children = activeId.HasValue
            ? await _workItemRepo.GetChildrenAsync(activeId.Value, ct)
            : [];

        // 4. Resolve iteration paths
        var iterations = iterationPaths
            ?? [await _iterationService.GetCurrentIterationAsync(ct)];

        // 5. Query sprint items (filtered by assignee when configured)
        var sprintItems = iterations.Count == 0
            ? (IReadOnlyList<WorkItem>)[]
            : await _workItemRepo.GetByIterationsAsync(iterations, ct);

        if (_userDisplayName is not null && sprintItems.Count > 0)
        {
            sprintItems = sprintItems
                .Where(w => string.Equals(w.AssignedTo, _userDisplayName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // 6. Query seeds
        var seeds = await _workItemRepo.GetSeedsAsync(ct);

        // 7. Query dirty IDs via SyncGuard
        var dirtyIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingStore, ct);

        // 8. Query tracked item IDs (when tracking repo is available)
        var trackedItemIds = _trackingRepo is not null
            ? (await _trackingRepo.GetAllTrackedAsync(ct)).Select(t => t.WorkItemId).ToList()
            : (IReadOnlyList<int>)[];

        return new WorkingSet
        {
            ActiveItemId = activeId,
            ParentChainIds = parentChain.Select(w => w.Id).ToList(),
            ChildrenIds = children.Select(w => w.Id).ToList(),
            SprintItemIds = sprintItems.Select(w => w.Id).ToList(),
            SeedIds = seeds.Select(w => w.Id).ToList(),
            DirtyItemIds = dirtyIds,
            TrackedItemIds = trackedItemIds,
            IterationPaths = iterations,
        };
    }
}
