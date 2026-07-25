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
    /// Repoints every staged change from <paramref name="oldId"/> to <paramref name="newId"/>.
    /// Used when a seed is published and its negative ID becomes a real ADO ID, so staged
    /// notes and field edits survive the publish and flush to the published item on the next
    /// sync — see PolyphonyRequiem/twig#270.
    /// </summary>
    /// <remarks>
    /// The row for <paramref name="newId"/> must already exist in <c>work_items</c>: the
    /// <c>pending_changes.work_item_id</c> FOREIGN KEY is enforced immediately.
    /// </remarks>
    Task RemapWorkItemIdAsync(int oldId, int newId, CancellationToken ct = default);

    /// <summary>
    /// Deletes all pending changes for non-seed work items, including orphaned rows
    /// whose work_item_id no longer exists. Returns the number of rows deleted.
    /// </summary>
    Task<int> ClearAllChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns counts of pending changes for the given work item, split by type:
    /// Notes (<c>note</c>, legacy <c>add_note</c>) and FieldEdits (<c>field</c>,
    /// <c>state</c>, legacy <c>set_field</c>). The legacy aliases are still honoured so
    /// rows written by older twig versions keep counting — see PolyphonyRequiem/twig#251,
    /// where a staged note reported a summary of zero because production writes
    /// <c>note</c> while this query only matched <c>add_note</c>.
    /// </summary>
    Task<(int Notes, int FieldEdits)> GetChangeSummaryAsync(int workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns the total number of pending change rows across all work items.
    /// </summary>
    Task<int> GetTotalPendingChangeCountAsync(CancellationToken ct = default);
}
