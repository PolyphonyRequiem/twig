using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IContextStore"/>.
/// Uses INSERT OR REPLACE on the context table for key-value storage.
/// </summary>
public sealed class SqliteContextStore : IContextStore
{
    private const string ActiveWorkItemKey = "active_work_item_id";
    private readonly SqliteCacheStore _store;

    public SqliteContextStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default)
    {
        var value = GetValue(ActiveWorkItemKey);
        if (value is not null && int.TryParse(value, out var id))
        {
            return Task.FromResult<int?>(id);
        }
        return Task.FromResult<int?>(null);
    }

    public Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default)
    {
        SetValue(ActiveWorkItemKey, id.ToString());
        return Task.CompletedTask;
    }

    public Task ClearActiveWorkItemIdAsync(CancellationToken ct = default)
    {
        DeleteKey(ActiveWorkItemKey);
        return Task.CompletedTask;
    }

    public Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(GetValue(key));
    }

    public Task SetValueAsync(string key, string value, CancellationToken ct = default)
    {
        SetValue(key, value);
        return Task.CompletedTask;
    }

    private string? GetValue(string key)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM context WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private void SetValue(string key, string value)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    private void DeleteKey(string key)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM context WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }
}
