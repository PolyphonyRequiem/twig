namespace Twig.Domain.Interfaces;

/// <summary>
/// Persistent store for the active work item context and key-value settings.
/// Implemented in Infrastructure (SQLite).
/// </summary>
/// <remarks>
/// <b>Navigation history:</b> Any command that calls <see cref="SetActiveWorkItemIdAsync"/>
/// as an explicit user-initiated context change should also call
/// <c>INavigationHistoryStore.RecordVisitAsync</c> afterward. Implicit/automatic
/// changes (e.g., git hook post-checkout) should NOT record history. See DD-11
/// in <c>twig-nav-history.plan.md</c> for rationale.
/// </remarks>
public interface IContextStore
{
    Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default);
    Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default);
    Task ClearActiveWorkItemIdAsync(CancellationToken ct = default);
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);
    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}
