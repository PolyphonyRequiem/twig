using Twig.Domain.Enums;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Domain service for managing tracked work items and Bench-backed pin operations.
/// Orchestrates calls to the current pin reader and writer.
/// </summary>
public interface ITrackingService
{
    /// <summary>Tracks a work item with the specified mode (Single or Tree). Upserts if already tracked.</summary>
    Task TrackAsync(int workItemId, TrackingMode mode, CancellationToken ct = default);

    /// <summary>Convenience: tracks a work item in Tree mode.</summary>
    Task TrackTreeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes a work item from tracking. Returns true if it was tracked, false if not.</summary>
    Task<bool> UntrackAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Returns all currently tracked items.</summary>
    Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default);

    /// <summary>
    /// Syncs all Tree-mode tracked items: re-explores each root via
    /// <see cref="SyncCoordinator.SyncItemAsync"/> and
    /// <see cref="SyncCoordinator.SyncChildrenAsync"/>, auto-untracks
    /// items that no longer exist in ADO.
    /// Returns the number of items that were auto-untracked.
    /// </summary>
    Task<int> SyncTrackedTreesAsync(SyncCoordinator syncCoordinator, CancellationToken ct = default);

    /// <summary>
    /// Evaluates the configured cleanup policy against all tracked items and removes
    /// those that match the policy criteria.
    /// <list type="bullet">
    /// <item><see cref="TrackingCleanupPolicy.None"/>: no-op.</item>
    /// <item><see cref="TrackingCleanupPolicy.OnComplete"/>: removes items whose state
    /// resolves to <see cref="Enums.StateCategory.Completed"/>.</item>
    /// <item><see cref="TrackingCleanupPolicy.OnCompleteAndPast"/>: removes items that are
    /// both completed and in a past iteration (iteration path ≠ <paramref name="currentIteration"/>).</item>
    /// </list>
    /// Returns the number of items removed.
    /// </summary>
    Task<int> ApplyCleanupPolicyAsync(
        TrackingCleanupPolicy policy,
        IterationPath currentIteration,
        CancellationToken ct = default);
}
