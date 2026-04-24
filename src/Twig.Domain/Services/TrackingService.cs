using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Domain service that orchestrates tracking and exclusion operations
/// by delegating to <see cref="ITrackingRepository"/>.
/// </summary>
public sealed class TrackingService(ITrackingRepository repository) : ITrackingService
{
    /// <inheritdoc />
    public Task TrackAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
        => repository.UpsertTrackedAsync(workItemId, mode, ct);

    /// <inheritdoc />
    public Task TrackTreeAsync(int workItemId, CancellationToken ct = default)
        => TrackAsync(workItemId, TrackingMode.Tree, ct);

    /// <inheritdoc />
    public Task UntrackAsync(int workItemId, CancellationToken ct = default)
        => repository.RemoveTrackedAsync(workItemId, ct);

    /// <inheritdoc />
    public Task ExcludeAsync(int workItemId, CancellationToken ct = default)
        => repository.AddExcludedAsync(workItemId, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default)
        => repository.GetAllTrackedAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetExcludedIdsAsync(CancellationToken ct = default)
    {
        var excluded = await repository.GetAllExcludedAsync(ct);
        return excluded.Select(e => e.WorkItemId).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExcludedItem>> ListExclusionsAsync(CancellationToken ct = default)
        => repository.GetAllExcludedAsync(ct);
}
