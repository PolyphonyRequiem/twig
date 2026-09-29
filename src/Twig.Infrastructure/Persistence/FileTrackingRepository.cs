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

    public static int PurgeLegacyExclusions(string filePath)
    {
        if (!File.Exists(filePath))
            return 0;

        return LoadTrackingFile(filePath, loadTracked: false).PurgedCount;
    }

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

    /// <summary>
    /// Lazily loads tracked rows, purging obsolete exclusion rows before reading.
    /// A missing file starts with no legacy tracked rows.
    /// </summary>
    private TrackingFile EnsureLoaded()
    {
        if (_cached is not null)
            return _cached;

        if (File.Exists(_filePath))
        {
            _cached = LoadTrackingFile(_filePath, loadTracked: true).File!;
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

    private static TrackingFileLoadResult LoadTrackingFile(string filePath, bool loadTracked)
    {
        using (var snapshot = JsonDocument.Parse(File.ReadAllText(filePath)))
        {
            if (!HasExclusions(snapshot.RootElement, out _))
                return new TrackingFileLoadResult(
                    loadTracked ? new TrackingFile { Tracked = ReadTracked(snapshot.RootElement) } : null, 0);
        }

        // Competing processes may have read the same legacy rows. Re-read under an OS-visible
        // lock so only the process that actually removes them emits the one-time notice.
        using var purgeLock = AcquirePurgeLock(filePath);
        using var current = JsonDocument.Parse(File.ReadAllText(filePath));
        if (!HasExclusions(current.RootElement, out var excludedElement))
            return new TrackingFileLoadResult(
                loadTracked ? new TrackingFile { Tracked = ReadTracked(current.RootElement) } : null, 0);

        var file = new TrackingFile { Tracked = ReadTracked(current.RootElement) };
        var purgedCount = excludedElement.GetArrayLength();

        var normalizedJson = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        var tempPath = filePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, normalizedJson);
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }

        if (purgedCount > 0)
            Console.Error.WriteLine($"Purged {purgedCount} legacy exclusion(s) from tracking.json; exclusions never changed workspace membership.");

        return new TrackingFileLoadResult(loadTracked ? file : null, purgedCount);
    }
    private static bool HasExclusions(JsonElement root, out JsonElement excluded)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("tracking.json must contain a JSON object at the root.");
        if (!TryGetPropertyIgnoreCase(root, "excluded", out excluded))
            return false;
        if (excluded.ValueKind != JsonValueKind.Array)
            throw new JsonException("tracking.json Excluded must be an array when present.");
        return true;
    }

    private static FileStream AcquirePurgeLock(string filePath)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return new FileStream(filePath + ".lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 49)
            {
                Thread.Sleep(20);
            }
        }
        throw new IOException($"Cannot acquire migration lock for {filePath}.");
    }


    private static List<TrackingFileEntry> ReadTracked(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "tracked", out var trackedElement))
            return [];
        if (trackedElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("tracking.json Tracked must be an array when present.");
        return DeserializeTrackedList(trackedElement);
    }

    private static List<TrackingFileEntry> DeserializeTrackedList(JsonElement trackedElement)
    {
        var tracked = JsonSerializer.Deserialize(trackedElement.GetRawText(), TwigJsonContext.Default.ListTrackingFileEntry)
            ?? throw new JsonException("tracking.json Tracked could not be deserialized.");

        return tracked;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static TrackedItem ToDomain(TrackingFileEntry entry) =>
        new(entry.Id,
            Enum.TryParse<TrackingMode>(entry.Mode, ignoreCase: true, out var mode) ? mode : TrackingMode.Single,
            ParseTimestamp(entry.AddedAt));

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var dt) ? dt : DateTimeOffset.MinValue;

    private readonly record struct TrackingFileLoadResult(TrackingFile? File, int PurgedCount);
}
