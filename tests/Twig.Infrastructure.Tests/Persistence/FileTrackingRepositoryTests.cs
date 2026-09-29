using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="FileTrackingRepository"/> — file-backed tracking persistence.
/// Uses temp directories for isolation; each test gets a fresh directory.
/// </summary>
public sealed class FileTrackingRepositoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TwigPaths _paths;

    public FileTrackingRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FileTrackingRepository CreateRepo() => new(_paths);

    private static string CreateLegacyTrackingJson(
        (int id, string mode, string addedAt)[]? tracked = null,
        (int id, string addedAt)[]? excluded = null)
    {
        var payload = new Dictionary<string, object?>();

        if (tracked is not null)
        {
            payload["tracked"] = tracked
                .Select(entry => new { id = entry.id, mode = entry.mode, addedAt = entry.addedAt })
                .ToArray();
        }

        if (excluded is not null)
        {
            payload["excluded"] = excluded
                .Select(entry => new { id = entry.id, addedAt = entry.addedAt })
                .ToArray();
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private string ReadTrackingJson() => File.ReadAllText(_paths.TrackingFilePath);

    // ──────────────────────── GetAllTrackedAsync ────────────────────────

    [Fact]
    public async Task GetAllTrackedAsync_NoFile_ReturnsEmptyList()
    {
        var repo = CreateRepo();
        var items = await repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllTrackedAsync_ReturnsItemsOrderedByTimestamp()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(10, TrackingMode.Single);
        await repo.UpsertTrackedAsync(20, TrackingMode.Tree);

        var items = await repo.GetAllTrackedAsync();

        items.Count.ShouldBe(2);
        items[0].WorkItemId.ShouldBe(10);
        items[0].Mode.ShouldBe(TrackingMode.Single);
        items[1].WorkItemId.ShouldBe(20);
        items[1].Mode.ShouldBe(TrackingMode.Tree);
    }

    // ──────────────────────── GetTrackedByWorkItemIdAsync ────────────────────────

    [Fact]
    public async Task GetTrackedByWorkItemIdAsync_NotFound_ReturnsNull()
    {
        var repo = CreateRepo();
        var item = await repo.GetTrackedByWorkItemIdAsync(999);
        item.ShouldBeNull();
    }

    [Fact]
    public async Task GetTrackedByWorkItemIdAsync_Found_ReturnsItem()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(42, TrackingMode.Tree);

        var item = await repo.GetTrackedByWorkItemIdAsync(42);

        item.ShouldNotBeNull();
        item.WorkItemId.ShouldBe(42);
        item.Mode.ShouldBe(TrackingMode.Tree);
        item.TrackedAt.ShouldNotBe(default);
    }

    // ──────────────────────── UpsertTrackedAsync ────────────────────────

    [Fact]
    public async Task UpsertTrackedAsync_Insert_CreatesNewItem()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        var items = await repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].WorkItemId.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Single);
    }

    [Fact]
    public async Task UpsertTrackedAsync_Update_OverwritesModePreservesTimestamp()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        var before = await repo.GetTrackedByWorkItemIdAsync(1);

        await repo.UpsertTrackedAsync(1, TrackingMode.Tree);
        var after = await repo.GetTrackedByWorkItemIdAsync(1);

        var items = await repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Tree);
        after!.TrackedAt.ShouldBe(before!.TrackedAt);
    }

    // ──────────────────────── RemoveTrackedAsync ────────────────────────

    [Fact]
    public async Task RemoveTrackedAsync_ExistingItem_RemovesIt()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.RemoveTrackedAsync(1);

        var items = await repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveTrackedAsync_NonExistent_NoOp()
    {
        var repo = CreateRepo();
        await repo.RemoveTrackedAsync(999);
        var items = await repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    // ──────────────────────── RemoveTrackedBatchAsync ────────────────────────

    [Fact]
    public async Task RemoveTrackedBatchAsync_EmptyList_NoOp()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.RemoveTrackedBatchAsync([]);

        var items = await repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveTrackedBatchAsync_RemovesOnlySpecifiedItems()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.UpsertTrackedAsync(2, TrackingMode.Tree);
        await repo.UpsertTrackedAsync(3, TrackingMode.Single);

        await repo.RemoveTrackedBatchAsync([1, 3]);

        var items = await repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].WorkItemId.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveTrackedBatchAsync_MixedExistingAndNonExistent_RemovesExisting()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        await repo.RemoveTrackedBatchAsync([1, 999]);

        var items = await repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    // ──────────────────────── Atomic write / persistence ────────────────────────

    [Fact]
    public async Task AtomicWrite_FileCreatedOnFirstWrite()
    {
        var repo = CreateRepo();
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();

        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        File.Exists(_paths.TrackingFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task AtomicWrite_NoTempFileLeftBehind()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        File.Exists(_paths.TrackingFilePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Persistence_DataSurvivesNewInstance()
    {
        var repo1 = CreateRepo();
        await repo1.UpsertTrackedAsync(1, TrackingMode.Single);

        // New instance reads from same file
        var repo2 = CreateRepo();
        var tracked = await repo2.GetAllTrackedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(1);
    }

    // ──────────────────────── Lazy loading / file parsing ────────────────────────

    [Fact]
    public async Task LazyLoading_ReadsFromExistingFile()
    {
        // Pre-create a tracking.json file
        var json = CreateLegacyTrackingJson(
            tracked: [(99, "tree", "2026-01-15T10:00:00+00:00")]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(99);
        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
    }

    [Fact]
    public async Task EmptyJsonFile_HandledGracefully()
    {
        File.WriteAllText(_paths.TrackingFilePath, "{}");

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidModeString_DefaultsToSingle()
    {
        var json = CreateLegacyTrackingJson(
            tracked: [(1, "unknown_mode", "2026-01-01T00:00:00Z")]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].Mode.ShouldBe(TrackingMode.Single);
    }

    [Fact]
    public async Task InvalidTimestamp_DefaultsToMinValue()
    {
        var json = CreateLegacyTrackingJson(
            tracked: [(1, "single", "not-a-date")]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].TrackedAt.ShouldBe(DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task DirectoryCreatedAutomatically_WhenDoesNotExist()
    {
        // Use a paths that points to a non-existent subdirectory
        var nestedDir = Path.Combine(_tempDir, "nested", "deep");
        var nestedPaths = new TwigPaths(nestedDir, Path.Combine(nestedDir, "config"), Path.Combine(nestedDir, "twig.db"));
        var repo = new FileTrackingRepository(nestedPaths);

        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        Directory.Exists(nestedDir).ShouldBeTrue();
        File.Exists(nestedPaths.TrackingFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task WrittenJson_IsValidAndHumanReadable()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(42, TrackingMode.Tree);

        var json = ReadTrackingJson();
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(1);
        deserialized.Tracked[0].Id.ShouldBe(42);
        deserialized.Tracked[0].Mode.ShouldBe("tree");
        json.ShouldContain("\"tracked\"");
        json.ShouldNotContain("\"excluded\"");
    }

    // ──────────────────────── Migration output validation ────────────────────────

    [Fact]
    public void PurgeLegacyExclusions_LegacyJson_PurgesExcludedEntriesAndReturnsCount()
    {
        var json = CreateLegacyTrackingJson(
            tracked:
            [
                (1, "single", "2026-01-01T00:00:00Z"),
                (2, "tree", "2026-01-02T00:00:00Z")
            ],
            excluded:
            [
                (10, "2026-01-03T00:00:00Z"),
                (20, "2026-01-04T00:00:00Z")
            ]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var purged = FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath);

        purged.ShouldBe(2);

        var after = ReadTrackingJson();
        after.ShouldContain("\"tracked\"");
        after.ShouldNotContain("\"excluded\"");

        var deserialized = JsonSerializer.Deserialize(after, TwigJsonContext.Default.TrackingFile);
        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(2);
        deserialized.Tracked[0].Id.ShouldBe(1);
        deserialized.Tracked[0].Mode.ShouldBe("single");
        deserialized.Tracked[1].Id.ShouldBe(2);
        deserialized.Tracked[1].Mode.ShouldBe("tree");
    }

    [Fact]
    public void PurgeLegacyExclusions_EmptyLegacyArray_RemovesDormantProperty()
    {
        File.WriteAllText(_paths.TrackingFilePath,
            CreateLegacyTrackingJson(excluded: []));

        FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath).ShouldBe(0);

        using var result = JsonDocument.Parse(ReadTrackingJson());
        result.RootElement.TryGetProperty("excluded", out _).ShouldBeFalse();
    }

    [Fact]
    public void PurgeLegacyExclusions_AlreadyPurged_ReturnsZeroAndKeepsTrackedOnly()
    {
        var json = CreateLegacyTrackingJson(
            tracked: [(1, "single", "2026-01-01T00:00:00Z")],
            excluded: [(7, "2026-01-02T00:00:00Z")]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath).ShouldBe(1);
        FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath).ShouldBe(0);

        var after = ReadTrackingJson();
        after.ShouldContain("\"tracked\"");
        after.ShouldNotContain("\"excluded\"");

        var deserialized = JsonSerializer.Deserialize(after, TwigJsonContext.Default.TrackingFile);
        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(1);
        deserialized.Tracked[0].Id.ShouldBe(1);
    }

    [Fact]
    public void PurgeLegacyExclusions_CleanTrackedOnlyFile_ReturnsZeroAndDoesNotInventData()
    {
        var json = CreateLegacyTrackingJson(
            tracked:
            [
                (1, "single", "2026-01-01T00:00:00Z"),
                (2, "tree", "2026-01-02T00:00:00Z")
            ]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var purged = FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath);

        purged.ShouldBe(0);

        var after = ReadTrackingJson();
        after.ShouldContain("\"tracked\"");
        after.ShouldNotContain("\"excluded\"");

        var deserialized = JsonSerializer.Deserialize(after, TwigJsonContext.Default.TrackingFile);
        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(2);
        deserialized.Tracked[0].Id.ShouldBe(1);
        deserialized.Tracked[1].Id.ShouldBe(2);
    }

    [Fact]
    public void PurgeLegacyExclusions_NoFile_ReturnsZeroAndLeavesNoFile()
    {
        var purged = FileTrackingRepository.PurgeLegacyExclusions(_paths.TrackingFilePath);

        purged.ShouldBe(0);
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();
    }


    // ──────────────────────── Ordering edge cases ────────────────────────

    [Fact]
    public async Task GetAllTrackedAsync_SameTimestamp_OrdersById()
    {
        // Pre-create file with entries sharing the same timestamp
        var json = CreateLegacyTrackingJson(
            tracked:
            [
                (30, "single", "2026-01-01T00:00:00Z"),
                (10, "tree", "2026-01-01T00:00:00Z"),
                (20, "single", "2026-01-01T00:00:00Z")
            ]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(3);
        tracked[0].WorkItemId.ShouldBe(10);
        tracked[1].WorkItemId.ShouldBe(20);
        tracked[2].WorkItemId.ShouldBe(30);
    }

    // ──────────────────────── Caching ────────────────────────

    [Fact]
    public async Task Caching_MultipleOperationsAccumulate()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.UpsertTrackedAsync(2, TrackingMode.Tree);

        var tracked = await repo.GetAllTrackedAsync();
        tracked.Count.ShouldBe(2);

        // Remove and verify cache reflects the change
        await repo.RemoveTrackedAsync(1);
        tracked = await repo.GetAllTrackedAsync();
        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(2);
    }

    // ──────────────────────── Mode storage ────────────────────────

    [Fact]
    public async Task UpsertTrackedAsync_StoresModeLowercase()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Tree);

        var json = ReadTrackingJson();
        var file = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile)!;

        file.Tracked[0].Mode.ShouldBe("tree");
    }

    [Fact]
    public async Task MixedCaseMode_InFile_ParsesCorrectly()
    {
        // Simulate a hand-edited file with mixed-case mode
        var json = CreateLegacyTrackingJson(
            tracked:
            [
                (1, "TREE", "2026-01-01T00:00:00Z"),
                (2, "Single", "2026-01-02T00:00:00Z")
            ]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        tracked[1].Mode.ShouldBe(TrackingMode.Single);
    }

    // ──────────────────────── Timestamp preservation ────────────────────────

    [Fact]
    public async Task UpsertTrackedAsync_SetsValidTimestamp()
    {
        var repo = CreateRepo();
        var before = DateTimeOffset.UtcNow;

        await repo.UpsertTrackedAsync(1, TrackingMode.Single);

        var tracked = await repo.GetAllTrackedAsync();
        tracked.Count.ShouldBe(1);
        tracked[0].TrackedAt.ShouldBeGreaterThanOrEqualTo(before);
        tracked[0].TrackedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
    }

    // ──────────────────────── RemoveTrackedAsync edge cases ────────────────────────

    [Fact]
    public async Task RemoveTrackedAsync_FromMultipleItems_LeavesOthersIntact()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.UpsertTrackedAsync(2, TrackingMode.Tree);
        await repo.UpsertTrackedAsync(3, TrackingMode.Single);

        await repo.RemoveTrackedAsync(2);

        var items = await repo.GetAllTrackedAsync();
        items.Count.ShouldBe(2);
        items.ShouldContain(t => t.WorkItemId == 1);
        items.ShouldContain(t => t.WorkItemId == 3);
    }

    // ──────────────────────── Empty AddedAt handling ────────────────────────

    [Fact]
    public async Task EmptyAddedAt_InFile_ParsesAsMinValue()
    {
        var json = CreateLegacyTrackingJson(
            tracked:
            [
                (1, "single", "")
            ]);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked[0].TrackedAt.ShouldBe(DateTimeOffset.MinValue);
    }
}