namespace Twig.Domain.Interfaces;

/// <summary>
/// Persistent store for the active work item context and key-value settings.
/// Implemented in Infrastructure (SQLite).
/// </summary>
public interface IContextStore
{
    Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default);
    Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default);
    Task ClearActiveWorkItemIdAsync(CancellationToken ct = default);
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);
    Task SetValueAsync(string key, string value, CancellationToken ct = default);
}
