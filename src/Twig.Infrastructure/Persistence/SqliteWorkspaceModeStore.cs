using Microsoft.Data.Sqlite;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="IWorkspaceModeStore"/>.
/// Stores workspace mode configuration in the context table and dedicated
/// tables for tracked items, excluded items, sprint iterations, and area paths.
/// </summary>
public sealed class SqliteWorkspaceModeStore(SqliteCacheStore store) : IWorkspaceModeStore
{
    private const string WorkspaceModeKey = "workspace_mode";

    public Task<WorkspaceMode> GetActiveModeAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM context WHERE key = @key;";
        cmd.Parameters.AddWithValue("@key", WorkspaceModeKey);
        var result = cmd.ExecuteScalar() as string;
        return Task.FromResult(WorkspaceMode.TryParse(result) ?? WorkspaceMode.Sprint);
    }

    public Task SetActiveModeAsync(WorkspaceMode mode, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO context (key, value) VALUES (@key, @value);";
        cmd.Parameters.AddWithValue("@key", WorkspaceModeKey);
        cmd.Parameters.AddWithValue("@value", mode.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, mode, created_at FROM tracked_items ORDER BY created_at;";
        using var reader = cmd.ExecuteReader();
        var items = new List<TrackedItem>();
        while (reader.Read())
        {
            items.Add(new TrackedItem(
                reader.GetInt32(0),
                Enum.Parse<TrackingMode>(reader.GetString(1), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(2))));
        }
        return Task.FromResult<IReadOnlyList<TrackedItem>>(items);
    }

    public Task AddTrackedItemAsync(int id, TrackingMode mode, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tracked_items (id, mode, created_at) VALUES (@id, @mode, @createdAt);";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@mode", mode.ToString());
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveTrackedItemAsync(int id, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracked_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetExcludedItemIdsAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM excluded_items ORDER BY created_at;";
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt32(0));
        }
        return Task.FromResult<IReadOnlyList<int>>(ids);
    }

    public Task AddExcludedItemAsync(int id, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO excluded_items (id, created_at) VALUES (@id, @createdAt);";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveExcludedItemAsync(int id, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM excluded_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SprintIterationEntry>> GetSprintIterationsAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT expression, type FROM sprint_iterations ORDER BY expression;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<SprintIterationEntry>();
        while (reader.Read())
        {
            entries.Add(new SprintIterationEntry(reader.GetString(0), reader.GetString(1)));
        }
        return Task.FromResult<IReadOnlyList<SprintIterationEntry>>(entries);
    }

    public Task SetSprintIterationsAsync(IReadOnlyList<SprintIterationEntry> entries, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        InTransaction(tx =>
        {
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM sprint_iterations;";
            deleteCmd.ExecuteNonQuery();

            foreach (var entry in entries)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = "INSERT INTO sprint_iterations (expression, type) VALUES (@expression, @type);";
                insertCmd.Parameters.AddWithValue("@expression", entry.Expression);
                insertCmd.Parameters.AddWithValue("@type", entry.Type);
                insertCmd.ExecuteNonQuery();
            }
        });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkspaceAreaPath>> GetAreaPathsAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, semantics FROM area_paths ORDER BY path;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<WorkspaceAreaPath>();
        while (reader.Read())
        {
            entries.Add(new WorkspaceAreaPath(reader.GetString(0), reader.GetString(1)));
        }
        return Task.FromResult<IReadOnlyList<WorkspaceAreaPath>>(entries);
    }

    public Task SetAreaPathsAsync(IReadOnlyList<WorkspaceAreaPath> entries, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        InTransaction(tx =>
        {
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM area_paths;";
            deleteCmd.ExecuteNonQuery();

            foreach (var entry in entries)
            {
                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = "INSERT INTO area_paths (path, semantics) VALUES (@path, @semantics);";
                insertCmd.Parameters.AddWithValue("@path", entry.Path);
                insertCmd.Parameters.AddWithValue("@semantics", entry.Semantics);
                insertCmd.ExecuteNonQuery();
            }
        });
        return Task.CompletedTask;
    }

    private void InTransaction(Action<Microsoft.Data.Sqlite.SqliteTransaction> work)
    {
        var conn = store.GetConnection();
        using var tx = conn.BeginTransaction();
        try { work(tx); tx.Commit(); }
        catch { tx.Rollback(); throw; }
    }
}
