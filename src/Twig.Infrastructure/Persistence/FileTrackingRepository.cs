using System.Text.Json;
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
    /// If the file does not exist, starts with an empty <see cref="TrackingFile"/>.
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
            _cached = new TrackingFile();
        }

        return _cached;
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
