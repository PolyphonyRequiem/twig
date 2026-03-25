using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Persistent store for navigation history — tracks the chronological sequence of
/// work item context changes for back/forward traversal.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface INavigationHistoryStore
{
    Task RecordVisitAsync(int workItemId, CancellationToken ct = default);
    Task<int?> GoBackAsync(CancellationToken ct = default);
    Task<int?> GoForwardAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<NavigationHistoryEntry> Entries, int? CursorEntryId)> GetHistoryAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
