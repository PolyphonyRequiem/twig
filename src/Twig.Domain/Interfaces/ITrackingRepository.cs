using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Sync;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Repository for persisting and querying manually tracked and excluded work items.
/// This is the data-access contract consumed by TrackingService.
/// </summary>
public interface ITrackingRepository
{
    /// <summary>Returns all tracked items, ordered by tracked-at timestamp.</summary>
    Task<IReadOnlyList<TrackedItem>> GetAllTrackedAsync(CancellationToken ct = default);

    /// <summary>Returns a single tracked item by work item ID, or null if not tracked.</summary>
    Task<TrackedItem?> GetTrackedByWorkItemIdAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Inserts or updates a tracked item with the given mode.</summary>
    Task UpsertTrackedAsync(int workItemId, TrackingMode mode, CancellationToken ct = default);

    /// <summary>Removes a single tracked item. No-op if not tracked.</summary>
    Task RemoveTrackedAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes multiple tracked items in a single operation.</summary>
    Task RemoveTrackedBatchAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default);

    /// <summary>Returns all excluded items, ordered by excluded-at timestamp.</summary>
    Task<IReadOnlyList<ExcludedItem>> GetAllExcludedAsync(CancellationToken ct = default);

    /// <summary>Adds a work item to the exclusion list. Idempotent (upsert).</summary>
    Task AddExcludedAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes a work item from the exclusion list. No-op if not excluded.</summary>
    Task RemoveExcludedAsync(int workItemId, CancellationToken ct = default);

    /// <summary>Removes all exclusions.</summary>
    Task ClearAllExcludedAsync(CancellationToken ct = default);
}
