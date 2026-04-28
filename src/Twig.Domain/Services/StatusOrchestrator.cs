using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;

namespace Twig.Domain.Services;

/// <summary>
/// Encapsulates active item resolution, pending change retrieval, working set computation,
/// and sync coordination into a single "status snapshot".
/// <c>StatusCommand</c> delegates to this service for data gathering, then handles display.
/// </summary>
public sealed class StatusOrchestrator(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinatorFactory syncCoordinatorFactory)
{
    /// <summary>
    /// Gathers a complete status snapshot: active item, pending changes, seeds, and working set.
    /// Returns <c>null</c> active ID if no context is set.
    /// </summary>
    public async Task<StatusSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId is null)
            return StatusSnapshot.NoContext();

        var resolveResult = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolveResult.TryGetWorkItem(out var item, out var unreachableId, out var unreachableReason))
        {
            return StatusSnapshot.Unreachable(activeId.Value, unreachableId, unreachableReason);
        }

        var pending = await pendingChangeStore.GetChangesAsync(item.Id, ct);
        var seeds = await workItemRepo.GetSeedsAsync(ct);

        return new StatusSnapshot
        {
            HasContext = true,
            ActiveId = activeId.Value,
            Item = item,
            PendingChanges = pending,
            Seeds = seeds,
        };
    }

    /// <summary>Syncs the working set for the given iteration path. Best-effort — does not throw.</summary>
    public async Task SyncWorkingSetAsync(WorkItem item, CancellationToken ct = default)
    {
        try
        {
            var workingSet = await workingSetService.ComputeAsync(item.IterationPath, ct);
            await syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(workingSet, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* sync is best-effort — don't fail the command */ }
    }
}

/// <summary>Result of a status snapshot operation.</summary>
public sealed class StatusSnapshot
{
    public bool HasContext { get; init; }
    public int ActiveId { get; init; }
    public WorkItem? Item { get; init; }
    public IReadOnlyList<PendingChangeRecord> PendingChanges { get; init; } = [];
    public IReadOnlyList<WorkItem> Seeds { get; init; } = [];

    // Error state
    public int? UnreachableId { get; init; }
    public string? UnreachableReason { get; init; }

    public bool IsSuccess => HasContext && Item is not null;

    public static StatusSnapshot NoContext() => new() { HasContext = false };

    public static StatusSnapshot Unreachable(int activeId, int? errorId, string? reason) => new()
    {
        HasContext = true,
        ActiveId = activeId,
        UnreachableId = errorId ?? activeId,
        UnreachableReason = reason,
    };
}
