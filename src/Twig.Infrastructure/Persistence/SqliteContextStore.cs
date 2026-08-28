using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IContextStore"/>.
/// <para>
/// AB#688: the storage is <b>split by durability</b>, not by convenience. The active-item
/// pointer lives in the durable store's single-row <c>active_context</c> table, because ADO
/// cannot rebuild "which item is this workspace standing on" and a mirror rebuild was silently
/// erasing it. The arbitrary key/value surface stays on the disposable mirror's <c>context</c>
/// table, because everything it holds — <c>last_refreshed_at</c>, the navigation cursor —
/// describes the mirror and must reset with it.
/// </para>
/// </summary>
public sealed class SqliteContextStore : IContextStore
{
    private readonly SqliteCacheStore _store;

    public SqliteContextStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task<int?> GetActiveWorkItemIdAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT work_item_id FROM {SqliteCacheStore.DurableSchema}.active_context WHERE id = 1;";
        var result = cmd.ExecuteScalar();
        return Task.FromResult(result is null or DBNull ? null : (int?)Convert.ToInt32(result));
    }

    public Task SetActiveWorkItemIdAsync(int id, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT OR REPLACE INTO {SqliteCacheStore.DurableSchema}.active_context (id, work_item_id, set_at)
            VALUES (1, @id, @now);
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task ClearActiveWorkItemIdAsync(CancellationToken ct = default)
    {
        // The row is kept and the pointer nulled rather than deleted: "no active item" is then a
        // value a reader can see, not an absent row it has to interpret.
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT OR REPLACE INTO {SqliteCacheStore.DurableSchema}.active_context (id, work_item_id, set_at)
            VALUES (1, NULL, @now);
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
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
        cmd.CommandText = "SELECT value FROM main.context WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    private void SetValue(string key, string value)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO main.context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }
}
