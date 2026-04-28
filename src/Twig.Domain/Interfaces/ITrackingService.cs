using Twig.Domain.Enums;
using Twig.Domain.Services;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Domain service for managing manually tracked and excluded work items.
/// Orchestrates calls to <see cref="ITrackingRepository"/>.
/// </summary>
public interface ITrackingService
{
    /// <summary>Tracks a work item with the specified mode (Single or Tree). Upserts if already tracked.</summary>
    Task TrackAsync(int workItemId, TrackingMode mode, CancellationToken ct = default);

    /// <summary>Convenience: tracks a work item in Tree mode.</summary>
    Task TrackTreeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes a work item from tracking. Returns true if it was tracked, false if not.</summary>
    Task<bool> UntrackAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Adds a work item to the exclusion list. Idempotent.</summary>
    Task ExcludeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Returns all currently tracked items.</summary>
    Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default);

    /// <summary>Returns the IDs of all excluded work items.</summary>
    Task<IReadOnlyList<int>> GetExcludedIdsAsync(CancellationToken ct = default);

    /// <summary>Returns all exclusion entries for display purposes.</summary>
    Task<IReadOnlyList<ExcludedItem>> ListExclusionsAsync(CancellationToken ct = default);

    /// <summary>Removes a single exclusion by work item ID. Returns true if it existed, false if not.</summary>
    Task<bool> RemoveExclusionAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes all exclusions. Returns the count of exclusions removed.</summary>
    Task<int> ClearExclusionsAsync(CancellationToken ct = default);

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
