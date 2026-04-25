using Twig.Domain.Common;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Store for pending (uncommitted) changes to work items.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface IPendingChangeStore
{
    Task AddChangeAsync(int workItemId, string changeType, string? fieldName, string? oldValue, string? newValue, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts multiple pending changes within a single transaction.
    /// Either all changes are persisted or none — prevents duplicate rows on retry after partial failure.
    /// </summary>
    Task AddChangesBatchAsync(int workItemId, IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)> changes, CancellationToken ct = default);

    Task<IReadOnlyList<PendingChangeRecord>> GetChangesAsync(int workItemId, CancellationToken ct = default);
    Task ClearChangesAsync(int workItemId, CancellationToken ct = default);
    Task ClearChangesByTypeAsync(int workItemId, string changeType, CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetDirtyItemIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes all pending changes for non-seed work items, including orphaned rows
    /// whose work_item_id no longer exists. Returns the number of rows deleted.
    /// </summary>
    Task<int> ClearAllChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns counts of pending changes for the given work item, split by type:
    /// Notes (<c>add_note</c>) and FieldEdits (<c>set_field</c>).
    /// </summary>
    Task<(int Notes, int FieldEdits)> GetChangeSummaryAsync(int workItemId, CancellationToken ct = default);
}
