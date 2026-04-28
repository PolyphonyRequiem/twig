using System.Text.Json;
using Shouldly;
using Twig.Domain.Enums;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="FileTrackingRepository"/> — file-backed ITrackingRepository implementation.
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
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private FileTrackingRepository CreateRepo() => new(_paths);

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

    // ──────────────────────── GetAllExcludedAsync ────────────────────────

    [Fact]
    public async Task GetAllExcludedAsync_NoFile_ReturnsEmptyList()
    {
        var repo = CreateRepo();
        var items = await repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllExcludedAsync_ReturnsItemsOrderedByTimestamp()
    {
        var repo = CreateRepo();
        await repo.AddExcludedAsync(10);
        await repo.AddExcludedAsync(20);

        var items = await repo.GetAllExcludedAsync();

        items.Count.ShouldBe(2);
        items[0].WorkItemId.ShouldBe(10);
        items[0].ExcludedAt.ShouldNotBe(default);
        items[1].WorkItemId.ShouldBe(20);
    }

    // ──────────────────────── AddExcludedAsync ────────────────────────

    [Fact]
    public async Task AddExcludedAsync_Idempotent_NoDuplicate()
    {
        var repo = CreateRepo();
        await repo.AddExcludedAsync(1);
        await repo.AddExcludedAsync(1);

        var items = await repo.GetAllExcludedAsync();
        items.Count.ShouldBe(1);
    }

    // ──────────────────────── RemoveExcludedAsync ────────────────────────

    [Fact]
    public async Task RemoveExcludedAsync_ExistingItem_RemovesIt()
    {
        var repo = CreateRepo();
        await repo.AddExcludedAsync(1);
        await repo.RemoveExcludedAsync(1);

        var items = await repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveExcludedAsync_NonExistent_NoOp()
    {
        var repo = CreateRepo();
        await repo.RemoveExcludedAsync(999);
        var items = await repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    // ──────────────────────── ClearAllExcludedAsync ────────────────────────

    [Fact]
    public async Task ClearAllExcludedAsync_RemovesAll()
    {
        var repo = CreateRepo();
        await repo.AddExcludedAsync(1);
        await repo.AddExcludedAsync(2);
        await repo.AddExcludedAsync(3);

        await repo.ClearAllExcludedAsync();

        var items = await repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClearAllExcludedAsync_EmptyFile_NoOp()
    {
        var repo = CreateRepo();
        await repo.ClearAllExcludedAsync();
        var items = await repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    // ──────────────────────── Cross-concern: independence ────────────────────────

    [Fact]
    public async Task TrackedAndExcluded_AreIndependent()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(42, TrackingMode.Single);
        await repo.AddExcludedAsync(42);

        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();
        tracked.Count.ShouldBe(1);
        excluded.Count.ShouldBe(1);

        await repo.RemoveTrackedAsync(42);
        tracked = await repo.GetAllTrackedAsync();
        excluded = await repo.GetAllExcludedAsync();
        tracked.ShouldBeEmpty();
        excluded.Count.ShouldBe(1);
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
        await repo1.AddExcludedAsync(2);

        // New instance reads from same file
        var repo2 = CreateRepo();
        var tracked = await repo2.GetAllTrackedAsync();
        var excluded = await repo2.GetAllExcludedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(1);
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(2);
    }

    // ──────────────────────── Lazy loading ────────────────────────

    [Fact]
    public async Task LazyLoading_ReadsFromExistingFile()
    {
        // Pre-create a tracking.json file
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 99, Mode = "tree", AddedAt = "2026-01-15T10:00:00+00:00" }],
            Excluded = [new ExclusionFileEntry { Id = 50, AddedAt = "2026-01-15T11:00:00+00:00" }]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(99);
        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(50);
    }

    // ──────────────────────── Edge cases ────────────────────────

    [Fact]
    public async Task EmptyJsonFile_HandledGracefully()
    {
        File.WriteAllText(_paths.TrackingFilePath, "{}");

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.ShouldBeEmpty();
        excluded.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidModeString_DefaultsToSingle()
    {
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 1, Mode = "unknown_mode", AddedAt = "2026-01-01T00:00:00Z" }]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].Mode.ShouldBe(TrackingMode.Single);
    }

    [Fact]
    public async Task InvalidTimestamp_DefaultsToMinValue()
    {
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 1, Mode = "single", AddedAt = "not-a-date" }]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
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
        await repo.AddExcludedAsync(7);

        var json = File.ReadAllText(_paths.TrackingFilePath);
        var deserialized = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile);

        deserialized.ShouldNotBeNull();
        deserialized.Tracked.Count.ShouldBe(1);
        deserialized.Tracked[0].Id.ShouldBe(42);
        deserialized.Tracked[0].Mode.ShouldBe("tree");
        deserialized.Excluded.Count.ShouldBe(1);
        deserialized.Excluded[0].Id.ShouldBe(7);
    }
}
