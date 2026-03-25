using Microsoft.Data.Sqlite;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="INavigationHistoryStore"/>.
/// Maintains a circular buffer of up to 50 navigation history entries with cursor tracking.
/// Cursor position is stored in the context table as key 'nav_history_cursor'.
/// </summary>
public sealed class SqliteNavigationHistoryStore : INavigationHistoryStore
{
    private const string CursorKey = "nav_history_cursor";
    private const int MaxEntries = 50;

    private readonly SqliteCacheStore _store;

    public SqliteNavigationHistoryStore(SqliteCacheStore store)
    {
        _store = store;
    }

    public Task RecordVisitAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        // Read cursor
        var cursorId = GetCursorId(conn);

        // Forward pruning: if cursor is not at head, delete everything after cursor
        if (cursorId is not null)
        {
            var headId = GetMaxId(conn);
            if (headId is not null && cursorId < headId)
            {
                using var deleteCmd = conn.CreateCommand();
                deleteCmd.Transaction = _store.ActiveTransaction;
                deleteCmd.CommandText = "DELETE FROM navigation_history WHERE id > @cursorId;";
                deleteCmd.Parameters.AddWithValue("@cursorId", cursorId.Value);
                deleteCmd.ExecuteNonQuery();
            }
        }

        // Insert new entry
        var visitedAt = DateTimeOffset.UtcNow.ToString("o");
        using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = _store.ActiveTransaction;
        insertCmd.CommandText = "INSERT INTO navigation_history (work_item_id, visited_at) VALUES (@workItemId, @visitedAt); SELECT last_insert_rowid();";
        insertCmd.Parameters.AddWithValue("@workItemId", workItemId);
        insertCmd.Parameters.AddWithValue("@visitedAt", visitedAt);
        var newId = Convert.ToInt64(insertCmd.ExecuteScalar());

        // Circular buffer: enforce max entries
        var count = GetRowCount(conn);
        if (count > MaxEntries)
        {
            using var trimCmd = conn.CreateCommand();
            trimCmd.Transaction = _store.ActiveTransaction;
            trimCmd.CommandText = "DELETE FROM navigation_history WHERE id = (SELECT MIN(id) FROM navigation_history);";
            trimCmd.ExecuteNonQuery();
        }

        // Update cursor to the new entry
        SetCursorId(conn, (int)newId);

        return Task.CompletedTask;
    }

    public Task<int?> GoBackAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        var cursorId = GetCursorId(conn);
        if (cursorId is null)
            return Task.FromResult<int?>(null);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT id, work_item_id FROM navigation_history WHERE id < @cursorId ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@cursorId", cursorId.Value);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<int?>(null);

        var prevId = reader.GetInt32(0);
        var workItemId = reader.GetInt32(1);
        reader.Close();

        SetCursorId(conn, prevId);
        return Task.FromResult<int?>(workItemId);
    }

    public Task<int?> GoForwardAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();
        var cursorId = GetCursorId(conn);
        if (cursorId is null)
            return Task.FromResult<int?>(null);

        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT id, work_item_id FROM navigation_history WHERE id > @cursorId ORDER BY id ASC LIMIT 1;";
        cmd.Parameters.AddWithValue("@cursorId", cursorId.Value);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return Task.FromResult<int?>(null);

        var nextId = reader.GetInt32(0);
        var workItemId = reader.GetInt32(1);
        reader.Close();

        SetCursorId(conn, nextId);
        return Task.FromResult<int?>(workItemId);
    }

    public Task<(IReadOnlyList<NavigationHistoryEntry> Entries, int? CursorEntryId)> GetHistoryAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        var entries = new List<NavigationHistoryEntry>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT id, work_item_id, visited_at FROM navigation_history ORDER BY id ASC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            var workItemId = reader.GetInt32(1);
            var visitedAt = DateTimeOffset.Parse(reader.GetString(2));
            entries.Add(new NavigationHistoryEntry(id, workItemId, visitedAt));
        }
        reader.Close();

        var cursorId = GetCursorId(conn);

        return Task.FromResult<(IReadOnlyList<NavigationHistoryEntry>, int?)>((entries, cursorId));
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        var conn = _store.GetConnection();

        using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = _store.ActiveTransaction;
        deleteCmd.CommandText = "DELETE FROM navigation_history;";
        deleteCmd.ExecuteNonQuery();

        using var cursorCmd = conn.CreateCommand();
        cursorCmd.Transaction = _store.ActiveTransaction;
        cursorCmd.CommandText = "DELETE FROM context WHERE key = @key;";
        cursorCmd.Parameters.AddWithValue("@key", CursorKey);
        cursorCmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    private int? GetCursorId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT value FROM context WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", CursorKey);
        var result = cmd.ExecuteScalar();
        if (result is string s && int.TryParse(s, out var id))
            return id;
        return null;
    }

    private void SetCursorId(SqliteConnection conn, int id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "INSERT OR REPLACE INTO context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", CursorKey);
        cmd.Parameters.AddWithValue("@value", id.ToString());
        cmd.ExecuteNonQuery();
    }

    private long? GetMaxId(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT MAX(id) FROM navigation_history;";
        var result = cmd.ExecuteScalar();
        if (result is long l)
            return l;
        return null;
    }

    private long GetRowCount(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = _store.ActiveTransaction;
        cmd.CommandText = "SELECT COUNT(*) FROM navigation_history;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
