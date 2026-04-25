using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IPendingChangeStore"/>.
/// Stores pending changes as rows in the pending_changes table with auto-increment IDs.
/// </summary>
public sealed class SqlitePendingChangeStore : IPendingChangeStore
{
    private readonly SqliteCacheStore _store;

    public SqlitePendingChangeStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task AddChangeAsync(int workItemId, string changeType, string? fieldName, string? oldValue, string? newValue, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
            VALUES (@workItemId, @changeType, @fieldName, @oldValue, @newValue, @createdAt);
            """;
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@changeType", changeType);
        cmd.Parameters.AddWithValue("@fieldName", (object?)fieldName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task AddChangesBatchAsync(int workItemId, IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return Task.CompletedTask;

        var conn = _store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var (changeType, fieldName, oldValue, newValue) in changes)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO pending_changes (work_item_id, change_type, field_name, old_value, new_value, created_at)
                    VALUES (@workItemId, @changeType, @fieldName, @oldValue, @newValue, @createdAt);
                    """;
                cmd.Parameters.AddWithValue("@workItemId", workItemId);
                cmd.Parameters.AddWithValue("@changeType", changeType);
                cmd.Parameters.AddWithValue("@fieldName", (object?)fieldName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@oldValue", (object?)oldValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@newValue", (object?)newValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@createdAt", now);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PendingChangeRecord>> GetChangesAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pending_changes WHERE work_item_id = @workItemId ORDER BY id;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        var changes = new List<PendingChangeRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(MapRow(reader));
        }
        return Task.FromResult<IReadOnlyList<PendingChangeRecord>>(changes);
    }

    public Task ClearChangesAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_changes WHERE work_item_id = @workItemId;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task ClearChangesByTypeAsync(int workItemId, string changeType, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_changes WHERE work_item_id = @workItemId AND change_type = @changeType;";
        cmd.Parameters.AddWithValue("@workItemId", workItemId);
        cmd.Parameters.AddWithValue("@changeType", changeType);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetDirtyItemIdsAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT work_item_id FROM pending_changes ORDER BY work_item_id;";

        var ids = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task<int> ClearAllChangesAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM pending_changes
            WHERE work_item_id NOT IN (SELECT id FROM work_items WHERE is_seed = 1);
            """;
        var count = cmd.ExecuteNonQuery();
        return Task.FromResult(count);
    }

    public Task<(int Notes, int FieldEdits)> GetChangeSummaryAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN change_type = 'add_note'  THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN change_type = 'set_field' THEN 1 ELSE 0 END), 0)
            FROM pending_changes
            WHERE work_item_id = @workItemId;
            """;
        cmd.Parameters.AddWithValue("@workItemId", workItemId);

        using var reader = cmd.ExecuteReader();
        reader.Read();
        return Task.FromResult((reader.GetInt32(0), reader.GetInt32(1)));
    }

    private static PendingChangeRecord MapRow(SqliteDataReader reader)
    {
        return new PendingChangeRecord(
            WorkItemId: reader.GetInt32(reader.GetOrdinal("work_item_id")),
            ChangeType: reader.GetString(reader.GetOrdinal("change_type")),
            FieldName: reader.IsDBNull(reader.GetOrdinal("field_name"))
                ? null
                : reader.GetString(reader.GetOrdinal("field_name")),
            OldValue: reader.IsDBNull(reader.GetOrdinal("old_value"))
                ? null
                : reader.GetString(reader.GetOrdinal("old_value")),
            NewValue: reader.IsDBNull(reader.GetOrdinal("new_value"))
                ? null
                : reader.GetString(reader.GetOrdinal("new_value")));
    }
}
