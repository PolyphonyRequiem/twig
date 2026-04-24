using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Persistent store for workspace mode configuration.
/// Supports reading/writing the active mode, tracked/excluded items,
/// sprint iteration entries, and area path entries.
/// </summary>
public interface IWorkspaceModeStore
{
    Task<WorkspaceMode> GetActiveModeAsync(CancellationToken ct = default);
    Task SetActiveModeAsync(WorkspaceMode mode, CancellationToken ct = default);

    Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default);
    Task AddTrackedItemAsync(int id, TrackingMode mode, CancellationToken ct = default);
    Task RemoveTrackedItemAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<int>> GetExcludedItemIdsAsync(CancellationToken ct = default);
    Task AddExcludedItemAsync(int id, CancellationToken ct = default);
    Task RemoveExcludedItemAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<SprintIterationEntry>> GetSprintIterationsAsync(CancellationToken ct = default);
    Task SetSprintIterationsAsync(IReadOnlyList<SprintIterationEntry> entries, CancellationToken ct = default);

    Task<IReadOnlyList<WorkspaceAreaPath>> GetAreaPathsAsync(CancellationToken ct = default);
    Task SetAreaPathsAsync(IReadOnlyList<WorkspaceAreaPath> entries, CancellationToken ct = default);
}
