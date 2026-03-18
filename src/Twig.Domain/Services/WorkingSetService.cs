using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

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
    private readonly string? _userDisplayName;

    public WorkingSetService(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        IPendingChangeStore pendingStore,
        IIterationService iterationService,
        string? userDisplayName)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _pendingStore = pendingStore;
        _iterationService = iterationService;
        _userDisplayName = userDisplayName;
    }

    /// <summary>
    /// Computes the current working set from cache state.
    /// When <paramref name="iterationPath"/> is provided it is used directly (no ADO call);
    /// otherwise <see cref="IIterationService.GetCurrentIterationAsync"/> is called.
    /// </summary>
    public async Task<WorkingSet> ComputeAsync(
        IterationPath? iterationPath = null, CancellationToken ct = default)
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

        // 4. Resolve iteration path
        var iteration = iterationPath ?? await _iterationService.GetCurrentIterationAsync(ct);

        // 5. Query sprint items (filtered by assignee when configured)
        var sprintItems = _userDisplayName is not null
            ? await _workItemRepo.GetByIterationAndAssigneeAsync(iteration, _userDisplayName, ct)
            : await _workItemRepo.GetByIterationAsync(iteration, ct);

        // 6. Query seeds
        var seeds = await _workItemRepo.GetSeedsAsync(ct);

        // 7. Query dirty IDs via SyncGuard
        var dirtyIds = await SyncGuard.GetProtectedItemIdsAsync(_workItemRepo, _pendingStore, ct);

        return new WorkingSet
        {
            ActiveItemId = activeId,
            ParentChainIds = parentChain.Select(w => w.Id).ToList(),
            ChildrenIds = children.Select(w => w.Id).ToList(),
            SprintItemIds = sprintItems.Select(w => w.Id).ToList(),
            SeedIds = seeds.Select(w => w.Id).ToList(),
            DirtyItemIds = dirtyIds,
            IterationPath = iteration,
        };
    }
}
