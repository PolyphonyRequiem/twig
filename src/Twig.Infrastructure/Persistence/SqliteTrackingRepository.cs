using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ITrackingRepository"/>.
/// Operates on the tracked_items and excluded_items tables using parameterized SQL.
/// </summary>
public sealed class SqliteTrackingRepository(SqliteCacheStore store) : ITrackingRepository
{
    public Task<IReadOnlyList<TrackedItem>> GetAllTrackedAsync(CancellationToken ct = default)
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

    public Task<TrackedItem?> GetTrackedByWorkItemIdAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, mode, created_at FROM tracked_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", workItemId);
        using var reader = cmd.ExecuteReader();
        TrackedItem? item = reader.Read()
            ? new TrackedItem(
                reader.GetInt32(0),
                Enum.Parse<TrackingMode>(reader.GetString(1), ignoreCase: true),
                DateTimeOffset.Parse(reader.GetString(2)))
            : null;

        return Task.FromResult(item);
    }

    public Task UpsertTrackedAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO tracked_items (id, mode, created_at) VALUES (@id, @mode, @createdAt);";
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.Parameters.AddWithValue("@mode", mode.ToString());
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveTrackedAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tracked_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveTrackedBatchAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0)
            return Task.CompletedTask;

        var conn = store.GetConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var id in workItemIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM tracked_items WHERE id = @id;";
                cmd.Parameters.AddWithValue("@id", id);
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

    public Task<IReadOnlyList<ExcludedItem>> GetAllExcludedAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, created_at FROM excluded_items ORDER BY created_at;";
        using var reader = cmd.ExecuteReader();
        var items = new List<ExcludedItem>();
        while (reader.Read())
        {
            items.Add(new ExcludedItem(
                reader.GetInt32(0),
                string.Empty,
                DateTimeOffset.Parse(reader.GetString(1))));
        }

        return Task.FromResult<IReadOnlyList<ExcludedItem>>(items);
    }

    public Task AddExcludedAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO excluded_items (id, created_at) VALUES (@id, @createdAt);";
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveExcludedAsync(int workItemId, CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM excluded_items WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", workItemId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task ClearAllExcludedAsync(CancellationToken ct = default)
    {
        var conn = store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM excluded_items;";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
