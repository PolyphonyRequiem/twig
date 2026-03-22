using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqliteWorkItemRepository: CRUD, queries, parent chain, pattern matching, batch save.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteWorkItemRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteWorkItemRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(999);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAndGetById_RoundTrip()
    {
        var item = CreateWorkItem(1, "Task", "Test Item", "Active");
        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(1);
        loaded.Title.ShouldBe("Test Item");
        loaded.State.ShouldBe("Active");
        loaded.Type.ShouldBe(WorkItemType.Task);
    }

    [Fact]
    public async Task GetChildrenAsync_ReturnsChildrenOrderedByTypeTitle()
    {
        var parent = CreateWorkItem(1, "Feature", "Parent", "Active");
        var child1 = CreateWorkItem(2, "Task", "Zebra Task", "Active", parentId: 1);
        var child2 = CreateWorkItem(3, "Bug", "Alpha Bug", "Active", parentId: 1);
        var child3 = CreateWorkItem(4, "Task", "Alpha Task", "Active", parentId: 1);

        await _repo.SaveAsync(parent);
        await _repo.SaveAsync(child1);
        await _repo.SaveAsync(child2);
        await _repo.SaveAsync(child3);

        var children = await _repo.GetChildrenAsync(1);
        children.Count.ShouldBe(3);
        // Bug < Task alphabetically, then sorted by title within type
        children[0].Title.ShouldBe("Alpha Bug");
        children[1].Title.ShouldBe("Alpha Task");
        children[2].Title.ShouldBe("Zebra Task");
    }

    [Fact]
    public async Task GetByIterationAsync_ReturnsMatchingItems()
    {
        var item1 = CreateWorkItem(1, "Task", "Item 1", "Active", iterationPath: @"Project\Sprint1");
        var item2 = CreateWorkItem(2, "Task", "Item 2", "Active", iterationPath: @"Project\Sprint2");

        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);

        var iterPath = IterationPath.Parse(@"Project\Sprint1");
        var results = await _repo.GetByIterationAsync(iterPath.Value);
        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetParentChainAsync_Returns3LevelChain_RootToParent()
    {
        var root = CreateWorkItem(1, "Epic", "Root Epic", "Active");
        var middle = CreateWorkItem(2, "Feature", "Middle Feature", "Active", parentId: 1);
        var leaf = CreateWorkItem(3, "Task", "Leaf Task", "Active", parentId: 2);

        await _repo.SaveAsync(root);
        await _repo.SaveAsync(middle);
        await _repo.SaveAsync(leaf);

        var chain = await _repo.GetParentChainAsync(3);
        chain.Count.ShouldBe(3);
        chain[0].Id.ShouldBe(1); // Root first
        chain[1].Id.ShouldBe(2); // Middle
        chain[2].Id.ShouldBe(3); // Leaf last
    }

    [Fact]
    public async Task FindByPatternAsync_CaseInsensitive()
    {
        var item1 = CreateWorkItem(1, "Task", "Fix Login Bug", "Active");
        var item2 = CreateWorkItem(2, "Task", "Add Feature", "Active");
        var item3 = CreateWorkItem(3, "Task", "fix logout bug", "Active");

        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);
        await _repo.SaveAsync(item3);

        var results = await _repo.FindByPatternAsync("fix");
        results.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetDirtyItemsAsync_ReturnsOnlyDirty()
    {
        var clean = CreateWorkItem(1, "Task", "Clean Item", "Active");
        var dirty = CreateWorkItem(2, "Task", "Dirty Item", "Active");
        dirty.UpdateField("System.Title", "Changed");
        dirty.ApplyCommands();

        await _repo.SaveAsync(clean);
        await _repo.SaveAsync(dirty);

        var dirtyItems = await _repo.GetDirtyItemsAsync();
        dirtyItems.Count.ShouldBe(1);
        dirtyItems[0].Id.ShouldBe(2);
    }

    [Fact]
    public async Task GetSeedsAsync_ReturnsOnlySeeds()
    {
        var regular = CreateWorkItem(1, "Task", "Regular Item", "Active");
        var seed = WorkItem.CreateSeed(WorkItemType.Task, "Seed Item");

        await _repo.SaveAsync(regular);
        await _repo.SaveAsync(seed);

        var seeds = await _repo.GetSeedsAsync();
        seeds.Count.ShouldBe(1);
        seeds[0].Title.ShouldBe("Seed Item");
        seeds[0].IsSeed.ShouldBeTrue();
    }

    [Fact]
    public async Task SaveBatchAsync_SavesMultipleItems()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Task", "Item 1", "Active"),
            CreateWorkItem(2, "Task", "Item 2", "Active"),
            CreateWorkItem(3, "Task", "Item 3", "Active"),
        };

        await _repo.SaveBatchAsync(items);

        var item1 = await _repo.GetByIdAsync(1);
        var item2 = await _repo.GetByIdAsync(2);
        var item3 = await _repo.GetByIdAsync(3);

        item1.ShouldNotBeNull();
        item2.ShouldNotBeNull();
        item3.ShouldNotBeNull();
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingItem()
    {
        var item = CreateWorkItem(1, "Task", "Original Title", "Active");
        await _repo.SaveAsync(item);

        // Title is init-only; create a new item with same ID but updated title
        var updated = CreateWorkItem(1, "Task", "Updated Title", "Active");
        await _repo.SaveAsync(updated);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.Title.ShouldBe("Updated Title");
    }

    [Fact]
    public async Task SaveAsync_PreservesFields()
    {
        var item = CreateWorkItem(1, "Task", "With Fields", "Active");
        item.SetField("Custom.Field", "custom_value");
        item.SetField("System.Description", "A description");

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.Fields.ShouldContainKey("Custom.Field");
        loaded.Fields["Custom.Field"].ShouldBe("custom_value");
    }

    [Fact]
    public async Task GetParentChainAsync_ReturnsEmptyForOrphan()
    {
        // Item with parent_id that doesn't exist in DB
        var chain = await _repo.GetParentChainAsync(999);
        chain.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ExistsByIdAsync_ReturnsTrueForExistingItem()
    {
        var item = CreateWorkItem(1, "Task", "Test Item", "Active");
        await _repo.SaveAsync(item);

        var exists = await _repo.ExistsByIdAsync(1);
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task ExistsByIdAsync_ReturnsFalseForMissing()
    {
        var exists = await _repo.ExistsByIdAsync(999);
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task GetOrphanParentIdsAsync_ReturnsParentIdsNotInCache()
    {
        // Insert a work item with parent_id=100, but don't insert parent 100
        var child = CreateWorkItem(1, "Task", "Orphan Child", "Active", parentId: 100);
        await _repo.SaveAsync(child);

        var orphans = await _repo.GetOrphanParentIdsAsync();
        orphans.Count.ShouldBe(1);
        orphans[0].ShouldBe(100);
    }

    [Fact]
    public async Task GetOrphanParentIdsAsync_ReturnsEmpty_WhenAllParentsCached()
    {
        var parent = CreateWorkItem(100, "Feature", "Parent", "Active");
        var child = CreateWorkItem(1, "Task", "Child", "Active", parentId: 100);
        await _repo.SaveAsync(parent);
        await _repo.SaveAsync(child);

        var orphans = await _repo.GetOrphanParentIdsAsync();
        orphans.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetOrphanParentIdsAsync_ExcludesNegativeIds()
    {
        // Seeds have negative IDs — their parent_id should still be detected if orphaned
        // But parent_id <= 0 is excluded by the SQL query
        var item = CreateWorkItem(1, "Task", "Item", "Active", parentId: -5);
        await _repo.SaveAsync(item);

        var orphans = await _repo.GetOrphanParentIdsAsync();
        orphans.Count.ShouldBe(0); // parent_id <= 0 excluded
    }

    [Fact]
    public async Task SaveAsync_DirtyItem_RoundTrip_DoesNotPolluteFields()
    {
        var item = CreateWorkItem(1, "Task", "Dirty Item", "Active");
        item.SetField("Custom.Field", "value");
        item.UpdateField("System.Title", "Changed Title");
        item.ApplyCommands();

        // Item is dirty with one custom field
        item.IsDirty.ShouldBeTrue();

        await _repo.SaveAsync(item);
        var loaded = await _repo.GetByIdAsync(1);

        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeTrue();
        loaded.Fields.ShouldContainKey("Custom.Field");
        loaded.Fields["Custom.Field"].ShouldBe("value");
        // No sentinel keys from dirty-flag restoration
        loaded.Fields.ShouldNotContainKey("__dirty_restore__");
    }

    [Fact]
    public async Task SaveAndGetById_CustomType_RoundTripsWithoutDataLoss()
    {
        var customType = WorkItemType.Parse("Scenario").Value;
        var item = new WorkItem
        {
            Id = 42,
            Type = customType,
            Title = "Custom Type Item",
            State = "Active",
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(42);
        loaded.ShouldNotBeNull();
        loaded.Type.Value.ShouldBe("Scenario");
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 5: SQLite busy timeout under contention
    //  Two threads: one doing SaveBatchAsync (large batch), one
    //  doing GetByIdAsync during the save. Verify: reader succeeds
    //  (WAL mode allows concurrent read), no SqliteException.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentReadDuringSaveBatch_WalModeAllowsConcurrentRead_NoException()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"twig_test_{Guid.NewGuid():N}.db");
        try
        {
            // Create the database and seed one item that the reader will query
            using var writerStore = new SqliteCacheStore($"Data Source={dbPath}");
            var writerRepo = new SqliteWorkItemRepository(writerStore);

            var seedItem = CreateWorkItem(999, "Task", "Seed Item", "Active");
            await writerRepo.SaveAsync(seedItem);

            // Create a second connection for reading (simulates concurrent access)
            using var readerStore = new SqliteCacheStore($"Data Source={dbPath}");
            var readerRepo = new SqliteWorkItemRepository(readerStore);

            // Prepare a large batch for the writer
            var largeBatch = Enumerable.Range(1, 100)
                .Select(i => CreateWorkItem(i, "Task", $"Batch Item {i}", "Active"))
                .ToList();

            // Run writer and reader concurrently
            var writerTask = Task.Run(async () =>
            {
                await writerRepo.SaveBatchAsync(largeBatch);
            });

            var readerTask = Task.Run(async () =>
            {
                // Attempt reads while the writer is saving
                for (var i = 0; i < 10; i++)
                {
                    var item = await readerRepo.GetByIdAsync(999);
                    item.ShouldNotBeNull();
                    item.Title.ShouldBe("Seed Item");
                    await Task.Delay(5);
                }
            });

            // Both should complete without SqliteException
            await Task.WhenAll(writerTask, readerTask);

            // Final consistency check: all batch items persisted
            for (var i = 1; i <= 100; i++)
            {
                var item = await readerRepo.GetByIdAsync(i);
                item.ShouldNotBeNull();
            }
        }
        finally
        {
            // Clean up temp files
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 6: Transaction rollback on partial batch save
    //  SaveBatchAsync with 10 items, trigger failure on item 7.
    //  Verify: transaction rolled back, items 1–6 NOT persisted,
    //  exception surfaced to caller.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchAsync_FailureOnItem7_RollsBackAllItems()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"twig_test_{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteCacheStore($"Data Source={dbPath}");
            var repo = new SqliteWorkItemRepository(store);

            // Add a trigger that rejects inserts for item with id=7
            var conn = store.GetConnection();
            using var triggerCmd = conn.CreateCommand();
            triggerCmd.CommandText = """
                CREATE TRIGGER fail_on_item_7
                BEFORE INSERT ON work_items
                WHEN NEW.id = 7
                BEGIN
                    SELECT RAISE(ABORT, 'Simulated failure on item 7');
                END;
                """;
            triggerCmd.ExecuteNonQuery();

            // Create 10 items, item 7 will trigger the failure
            var items = Enumerable.Range(1, 10)
                .Select(i => CreateWorkItem(i, "Task", $"Item {i}", "Active"))
                .ToList();

            // SaveBatchAsync should throw due to the trigger
            var ex = await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(
                () => repo.SaveBatchAsync(items));
            ex.Message.ShouldContain("Simulated failure on item 7");

            // Transaction was rolled back: items 1–6 should NOT be persisted
            for (var i = 1; i <= 10; i++)
            {
                var loaded = await repo.GetByIdAsync(i);
                loaded.ShouldBeNull($"Item {i} should not be persisted after rollback");
            }
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(dbPath) + "*"))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
    }

    private static WorkItem CreateWorkItem(
        int id, string type, string title, string state,
        int? parentId = null, string? assignedTo = null,
        string? iterationPath = null, string? areaPath = null)
    {
        var typeResult = WorkItemType.Parse(type);
        var iterResult = IterationPath.Parse(iterationPath ?? @"Project\Sprint1");
        var areaResult = AreaPath.Parse(areaPath ?? @"Project\Area");

        return new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = title,
            State = state,
            ParentId = parentId,
            AssignedTo = assignedTo,
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
    }
}
