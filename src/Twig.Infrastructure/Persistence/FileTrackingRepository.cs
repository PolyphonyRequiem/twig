using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// File-backed implementation of <see cref="ITrackingRepository"/> backed by <c>tracking.json</c>.
/// Uses lazy loading (first access reads from disk) and atomic writes (serialize → temp file → rename).
/// </summary>
public sealed class FileTrackingRepository(TwigPaths paths) : ITrackingRepository
{
    private readonly string _filePath = paths.TrackingFilePath;
    private TrackingFile? _cached;

    public Task<IReadOnlyList<TrackedItem>> GetAllTrackedAsync(CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        var items = file.Tracked
            .OrderBy(e => e.AddedAt, StringComparer.Ordinal)
            .ThenBy(e => e.Id)
            .Select(e => ToDomain(e))
            .ToList();
        return Task.FromResult<IReadOnlyList<TrackedItem>>(items);
    }

    public Task<TrackedItem?> GetTrackedByWorkItemIdAsync(int workItemId, CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        var entry = file.Tracked.Find(e => e.Id == workItemId);
        return Task.FromResult(entry is null ? null : ToDomain(entry));
    }

    public Task UpsertTrackedAsync(int workItemId, TrackingMode mode, CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        var existing = file.Tracked.Find(e => e.Id == workItemId);
        if (existing is not null)
        {
            existing.Mode = mode.ToString().ToLowerInvariant();
        }
        else
        {
            file.Tracked.Add(new TrackingFileEntry
            {
                Id = workItemId,
                Mode = mode.ToString().ToLowerInvariant(),
                AddedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        Save(file);
        return Task.CompletedTask;
    }

    public Task RemoveTrackedAsync(int workItemId, CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        file.Tracked.RemoveAll(e => e.Id == workItemId);
        Save(file);
        return Task.CompletedTask;
    }

    public Task RemoveTrackedBatchAsync(IReadOnlyList<int> workItemIds, CancellationToken ct = default)
    {
        if (workItemIds.Count == 0)
            return Task.CompletedTask;

        var file = EnsureLoaded();
        var idsToRemove = new HashSet<int>(workItemIds);
        file.Tracked.RemoveAll(e => idsToRemove.Contains(e.Id));
        Save(file);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExcludedItem>> GetAllExcludedAsync(CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        var items = file.Excluded
            .OrderBy(e => e.AddedAt, StringComparer.Ordinal)
            .ThenBy(e => e.Id)
            .Select(e => new ExcludedItem(e.Id, string.Empty, ParseTimestamp(e.AddedAt)))
            .ToList();
        return Task.FromResult<IReadOnlyList<ExcludedItem>>(items);
    }

    public Task AddExcludedAsync(int workItemId, CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        var existing = file.Excluded.Find(e => e.Id == workItemId);
        if (existing is null)
        {
            file.Excluded.Add(new ExclusionFileEntry
            {
                Id = workItemId,
                AddedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }

        Save(file);
        return Task.CompletedTask;
    }

    public Task RemoveExcludedAsync(int workItemId, CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        file.Excluded.RemoveAll(e => e.Id == workItemId);
        Save(file);
        return Task.CompletedTask;
    }

    public Task ClearAllExcludedAsync(CancellationToken ct = default)
    {
        var file = EnsureLoaded();
        file.Excluded.Clear();
        Save(file);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lazily loads the tracking file on first access.
    /// If the file does not exist, attempts a one-time migration from SQLite.
    /// If no SQLite data is found, starts with an empty <see cref="TrackingFile"/>.
    /// </summary>
    private TrackingFile EnsureLoaded()
    {
        if (_cached is not null)
            return _cached;

        if (File.Exists(_filePath))
        {
            var json = File.ReadAllText(_filePath);
            _cached = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile) ?? new TrackingFile();
        }
        else
        {
            _cached = TryMigrateFromSqlite() ?? new TrackingFile();
        }

        return _cached;
    }

    /// <summary>
    /// Attempts a one-time migration of tracking data from the SQLite database to <c>tracking.json</c>.
    /// Returns the migrated <see cref="TrackingFile"/> if data was found, or <c>null</c> if no migration occurred.
    /// SQLite tables are left inert — schema drop-recreate handles cleanup.
    /// </summary>
    private TrackingFile? TryMigrateFromSqlite()
    {
        var dbPath = paths.DbPath;
        if (!File.Exists(dbPath))
            return null;

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            connection.Open();

            if (!TableExists(connection, "tracked_items") && !TableExists(connection, "excluded_items"))
                return null;

            var tracked = ReadTrackedItems(connection);
            var excluded = ReadExcludedItems(connection);

            if (tracked.Count == 0 && excluded.Count == 0)
                return null;

            var file = new TrackingFile { Tracked = tracked, Excluded = excluded };
            Save(file);

            Console.Error.WriteLine($"Migrated tracking data from SQLite → tracking.json ({tracked.Count} tracked, {excluded.Count} excluded)");
            return file;
        }
        catch (SqliteException)
        {
            // DB may be corrupt or inaccessible — skip migration silently
            return null;
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static List<TrackingFileEntry> ReadTrackedItems(SqliteConnection connection)
    {
        if (!TableExists(connection, "tracked_items"))
            return [];

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, mode, created_at FROM tracked_items ORDER BY created_at, id;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<TrackingFileEntry>();
        while (reader.Read())
        {
            entries.Add(new TrackingFileEntry
            {
                Id = reader.GetInt32(0),
                Mode = reader.GetString(1).ToLowerInvariant(),
                AddedAt = reader.GetString(2)
            });
        }

        return entries;
    }

    private static List<ExclusionFileEntry> ReadExcludedItems(SqliteConnection connection)
    {
        if (!TableExists(connection, "excluded_items"))
            return [];

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, created_at FROM excluded_items ORDER BY created_at, id;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<ExclusionFileEntry>();
        while (reader.Read())
        {
            entries.Add(new ExclusionFileEntry
            {
                Id = reader.GetInt32(0),
                AddedAt = reader.GetString(1)
            });
        }

        return entries;
    }

    /// <summary>
    /// Atomically writes the tracking file: serialize → write temp file → rename over original.
    /// This prevents data corruption if the process is interrupted mid-write.
    /// </summary>
    private void Save(TrackingFile file)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    private static TrackedItem ToDomain(TrackingFileEntry entry) =>
        new(entry.Id,
            Enum.TryParse<TrackingMode>(entry.Mode, ignoreCase: true, out var mode) ? mode : TrackingMode.Single,
            ParseTimestamp(entry.AddedAt));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;
}
