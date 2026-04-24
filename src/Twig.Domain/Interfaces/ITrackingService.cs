using Twig.Domain.Enums;
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

    /// <summary>Removes a work item from tracking. No-op if not tracked.</summary>
    Task UntrackAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Adds a work item to the exclusion list. Idempotent.</summary>
    Task ExcludeAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Returns all currently tracked items.</summary>
    Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default);

    /// <summary>Returns the IDs of all excluded work items.</summary>
    Task<IReadOnlyList<int>> GetExcludedIdsAsync(CancellationToken ct = default);

    /// <summary>Returns all exclusion entries for display purposes.</summary>
    Task<IReadOnlyList<ExcludedItem>> ListExclusionsAsync(CancellationToken ct = default);
}
