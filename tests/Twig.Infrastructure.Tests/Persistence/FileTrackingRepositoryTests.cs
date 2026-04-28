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
        SqliteConnection.ClearAllPools();
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

    // ──────────────────────── SQLite → JSON migration ────────────────────────

    /// <summary>
    /// Creates a SQLite database at <paramref name="dbPath"/> with tracked_items and excluded_items tables,
    /// populated with the given data. Used to test the one-time migration path.
    /// </summary>
    private static void CreateSqliteWithTrackingData(
        string dbPath,
        (int id, string mode, string createdAt)[]? tracked = null,
        (int id, string createdAt)[]? excluded = null)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE tracked_items (
                id INTEGER PRIMARY KEY,
                mode TEXT NOT NULL DEFAULT 'single',
                created_at TEXT NOT NULL
            );
            CREATE TABLE excluded_items (
                id INTEGER PRIMARY KEY,
                created_at TEXT NOT NULL
            );
            """;
        schemaCmd.ExecuteNonQuery();

        if (tracked is not null)
        {
            foreach (var (id, mode, createdAt) in tracked)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO tracked_items (id, mode, created_at) VALUES (@id, @mode, @createdAt);";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@mode", mode);
                cmd.Parameters.AddWithValue("@createdAt", createdAt);
                cmd.ExecuteNonQuery();
            }
        }

        if (excluded is not null)
        {
            foreach (var (id, createdAt) in excluded)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO excluded_items (id, created_at) VALUES (@id, @createdAt);";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@createdAt", createdAt);
                cmd.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public async Task Migration_SqliteWithData_MigratesToJson()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(10, "Single", "2026-01-01T00:00:00Z"), (20, "Tree", "2026-01-02T00:00:00Z")],
            excluded: [(30, "2026-01-03T00:00:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(2);
        tracked[0].WorkItemId.ShouldBe(10);
        tracked[0].Mode.ShouldBe(TrackingMode.Single);
        tracked[1].WorkItemId.ShouldBe(20);
        tracked[1].Mode.ShouldBe(TrackingMode.Tree);
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(30);

        // Verify tracking.json was created
        File.Exists(_paths.TrackingFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_SqliteWithTrackedOnly_MigratesTrackedItems()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(42, "Tree", "2026-02-15T10:30:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(42);
        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        excluded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Migration_SqliteWithExcludedOnly_MigratesExcludedItems()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            excluded: [(7, "2026-03-01T08:00:00Z"), (14, "2026-03-02T09:00:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.ShouldBeEmpty();
        excluded.Count.ShouldBe(2);
        excluded[0].WorkItemId.ShouldBe(7);
        excluded[1].WorkItemId.ShouldBe(14);
    }

    [Fact]
    public async Task Migration_SqliteEmptyTables_NoMigrationNoFile()
    {
        CreateSqliteWithTrackingData(_paths.DbPath);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.ShouldBeEmpty();
        excluded.ShouldBeEmpty();
        // No file created when no data to migrate
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_NoSqliteDb_NoMigration()
    {
        // Ensure no DB exists
        File.Exists(_paths.DbPath).ShouldBeFalse();

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.ShouldBeEmpty();
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_TrackingJsonAlreadyExists_SkipsMigration()
    {
        // Pre-create tracking.json with different data
        var existingFile = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 99, Mode = "single", AddedAt = "2026-06-01T00:00:00Z" }]
        };
        var json = JsonSerializer.Serialize(existingFile, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        // Create SQLite with different data
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(1, "Tree", "2026-01-01T00:00:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        // Should use existing JSON, not migrate from SQLite
        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(99);
    }

    [Fact]
    public async Task Migration_CorruptSqliteDb_SkipsMigrationGracefully()
    {
        // Write garbage to the DB path
        var dbDir = Path.GetDirectoryName(_paths.DbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);
        File.WriteAllText(_paths.DbPath, "this is not a valid sqlite database");

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        // Should fall back to empty — no crash
        tracked.ShouldBeEmpty();
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_IsOneTime_SubsequentAccessesUseCachedFile()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(5, "Single", "2026-04-01T00:00:00Z")]);

        // First access migrates
        var repo1 = CreateRepo();
        var tracked1 = await repo1.GetAllTrackedAsync();
        tracked1.Count.ShouldBe(1);

        // Modify the JSON file to add another item
        await repo1.UpsertTrackedAsync(100, TrackingMode.Tree);

        // New instance reads from JSON, not SQLite
        var repo2 = CreateRepo();
        var tracked2 = await repo2.GetAllTrackedAsync();
        tracked2.Count.ShouldBe(2);
        tracked2.ShouldContain(t => t.WorkItemId == 5);
        tracked2.ShouldContain(t => t.WorkItemId == 100);
    }

    [Fact]
    public async Task Migration_PreservesTimestamps()
    {
        var timestamp = "2026-05-15T14:30:00+00:00";
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(1, "Single", timestamp)],
            excluded: [(2, timestamp)]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked[0].TrackedAt.ShouldBe(DateTimeOffset.Parse(timestamp));
        excluded[0].ExcludedAt.ShouldBe(DateTimeOffset.Parse(timestamp));
    }

    [Fact]
    public async Task Migration_SqliteWithoutTrackingTables_NoMigration()
    {
        // Create a DB without the tracking tables
        var dbDir = Path.GetDirectoryName(_paths.DbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

        using var connection = new SqliteConnection($"Data Source={_paths.DbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        connection.Close();

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.ShouldBeEmpty();
        File.Exists(_paths.TrackingFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_SqliteOnlyTrackedTable_MigratesTrackedItems()
    {
        // Create a SQLite DB with only tracked_items (no excluded_items table at all)
        var dbDir = Path.GetDirectoryName(_paths.DbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

        using var connection = new SqliteConnection($"Data Source={_paths.DbPath}");
        connection.Open();
        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE tracked_items (
                id INTEGER PRIMARY KEY,
                mode TEXT NOT NULL DEFAULT 'single',
                created_at TEXT NOT NULL
            );
            """;
        schemaCmd.ExecuteNonQuery();
        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO tracked_items (id, mode, created_at) VALUES (42, 'tree', '2026-01-01T00:00:00Z');";
        insertCmd.ExecuteNonQuery();
        connection.Close();

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(1);
        tracked[0].WorkItemId.ShouldBe(42);
        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        excluded.ShouldBeEmpty();
        File.Exists(_paths.TrackingFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_SqliteOnlyExcludedTable_MigratesExcludedItems()
    {
        // Create a SQLite DB with only excluded_items (no tracked_items table at all)
        var dbDir = Path.GetDirectoryName(_paths.DbPath);
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

        using var connection = new SqliteConnection($"Data Source={_paths.DbPath}");
        connection.Open();
        using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = """
            CREATE TABLE excluded_items (
                id INTEGER PRIMARY KEY,
                created_at TEXT NOT NULL
            );
            """;
        schemaCmd.ExecuteNonQuery();
        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO excluded_items (id, created_at) VALUES (99, '2026-02-01T00:00:00Z');";
        insertCmd.ExecuteNonQuery();
        connection.Close();

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.ShouldBeEmpty();
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(99);
        File.Exists(_paths.TrackingFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_ModeNormalizedToLowercase()
    {
        // SQLite data may have mixed-case mode strings; migration should normalize
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(1, "TREE", "2026-01-01T00:00:00Z"), (2, "Single", "2026-01-02T00:00:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(2);
        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        tracked[1].Mode.ShouldBe(TrackingMode.Single);

        // Verify stored JSON has lowercase mode strings
        var json = File.ReadAllText(_paths.TrackingFilePath);
        var file = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile)!;
        file.Tracked[0].Mode.ShouldBe("tree");
        file.Tracked[1].Mode.ShouldBe("single");
    }

    // ──────────────────────── Ordering edge cases ────────────────────────

    [Fact]
    public async Task GetAllTrackedAsync_SameTimestamp_OrdersById()
    {
        // Pre-create file with entries sharing the same timestamp
        var file = new TrackingFile
        {
            Tracked =
            [
                new TrackingFileEntry { Id = 30, Mode = "single", AddedAt = "2026-01-01T00:00:00Z" },
                new TrackingFileEntry { Id = 10, Mode = "tree", AddedAt = "2026-01-01T00:00:00Z" },
                new TrackingFileEntry { Id = 20, Mode = "single", AddedAt = "2026-01-01T00:00:00Z" }
            ]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked.Count.ShouldBe(3);
        tracked[0].WorkItemId.ShouldBe(10);
        tracked[1].WorkItemId.ShouldBe(20);
        tracked[2].WorkItemId.ShouldBe(30);
    }

    [Fact]
    public async Task GetAllExcludedAsync_SameTimestamp_OrdersById()
    {
        var file = new TrackingFile
        {
            Excluded =
            [
                new ExclusionFileEntry { Id = 30, AddedAt = "2026-01-01T00:00:00Z" },
                new ExclusionFileEntry { Id = 10, AddedAt = "2026-01-01T00:00:00Z" },
                new ExclusionFileEntry { Id = 20, AddedAt = "2026-01-01T00:00:00Z" }
            ]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var excluded = await repo.GetAllExcludedAsync();

        excluded.Count.ShouldBe(3);
        excluded[0].WorkItemId.ShouldBe(10);
        excluded[1].WorkItemId.ShouldBe(20);
        excluded[2].WorkItemId.ShouldBe(30);
    }

    // ──────────────────────── Caching ────────────────────────

    [Fact]
    public async Task Caching_MultipleOperationsAccumulate()
    {
        var repo = CreateRepo();
        await repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await repo.UpsertTrackedAsync(2, TrackingMode.Tree);
        await repo.AddExcludedAsync(3);

        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(2);
        excluded.Count.ShouldBe(1);

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

        var json = File.ReadAllText(_paths.TrackingFilePath);
        var file = JsonSerializer.Deserialize(json, TwigJsonContext.Default.TrackingFile)!;

        file.Tracked[0].Mode.ShouldBe("tree");
    }

    [Fact]
    public async Task MixedCaseMode_InFile_ParsesCorrectly()
    {
        // Simulate a hand-edited file with mixed-case mode
        var file = new TrackingFile
        {
            Tracked =
            [
                new TrackingFileEntry { Id = 1, Mode = "TREE", AddedAt = "2026-01-01T00:00:00Z" },
                new TrackingFileEntry { Id = 2, Mode = "Single", AddedAt = "2026-01-02T00:00:00Z" }
            ]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();

        tracked[0].Mode.ShouldBe(TrackingMode.Tree);
        tracked[1].Mode.ShouldBe(TrackingMode.Single);
    }

    // ──────────────────────── Excluded timestamp preservation ────────────────────────

    [Fact]
    public async Task AddExcludedAsync_SetsValidTimestamp()
    {
        var repo = CreateRepo();
        var before = DateTimeOffset.UtcNow;

        await repo.AddExcludedAsync(1);

        var excluded = await repo.GetAllExcludedAsync();
        excluded.Count.ShouldBe(1);
        excluded[0].ExcludedAt.ShouldBeGreaterThanOrEqualTo(before);
        excluded[0].ExcludedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddSeconds(1));
    }

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

    // ──────────────────────── Migration output validation ────────────────────────

    [Fact]
    public async Task Migration_WritesValidJsonReadableByNewInstance()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(1, "single", "2026-03-01T00:00:00Z"), (2, "tree", "2026-03-02T00:00:00Z")],
            excluded: [(3, "2026-03-03T00:00:00Z")]);

        // First instance migrates
        var repo1 = CreateRepo();
        await repo1.GetAllTrackedAsync();

        // Second instance reads the migrated file
        var repo2 = CreateRepo();
        var tracked = await repo2.GetAllTrackedAsync();
        var excluded = await repo2.GetAllExcludedAsync();

        tracked.Count.ShouldBe(2);
        tracked[0].WorkItemId.ShouldBe(1);
        tracked[1].WorkItemId.ShouldBe(2);
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(3);
    }

    [Fact]
    public async Task Migration_MigratedDataCanBeModified()
    {
        CreateSqliteWithTrackingData(
            _paths.DbPath,
            tracked: [(10, "single", "2026-01-01T00:00:00Z")]);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        tracked.Count.ShouldBe(1);

        // Add more items after migration
        await repo.UpsertTrackedAsync(20, TrackingMode.Tree);
        await repo.AddExcludedAsync(30);

        tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(2);
        tracked.ShouldContain(t => t.WorkItemId == 10);
        tracked.ShouldContain(t => t.WorkItemId == 20);
        excluded.Count.ShouldBe(1);
        excluded[0].WorkItemId.ShouldBe(30);
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
        var file = new TrackingFile
        {
            Tracked = [new TrackingFileEntry { Id = 1, Mode = "single", AddedAt = "" }],
            Excluded = [new ExclusionFileEntry { Id = 2, AddedAt = "" }]
        };
        var json = JsonSerializer.Serialize(file, TwigJsonContext.Default.TrackingFile);
        File.WriteAllText(_paths.TrackingFilePath, json);

        var repo = CreateRepo();
        var tracked = await repo.GetAllTrackedAsync();
        var excluded = await repo.GetAllExcludedAsync();

        tracked[0].TrackedAt.ShouldBe(DateTimeOffset.MinValue);
        excluded[0].ExcludedAt.ShouldBe(DateTimeOffset.MinValue);
    }
}
