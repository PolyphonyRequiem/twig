using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
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
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
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
        var seed = new WorkItemBuilder(-1, "Seed Item").AsSeed().Build();

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
    public async Task SaveAsync_PopulatedFields_RoundTripsAllEntries()
    {
        var item = CreateWorkItem(1, "Task", "Field enrichment test", "Active");
        item.SetField("Microsoft.VSTS.Common.Priority", "2");
        item.SetField("System.CreatedBy", "Alice");
        item.SetField("System.ChangedDate", "2025-01-15T10:30:00Z");
        item.SetField("System.Tags", "backend; api");
        item.SetField("System.Description", "<p>HTML content</p>");
        item.SetField("Custom.Team", "Platform");

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.Fields.Count.ShouldBe(6);
        loaded.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        loaded.Fields["System.CreatedBy"].ShouldBe("Alice");
        loaded.Fields["System.ChangedDate"].ShouldBe("2025-01-15T10:30:00Z");
        loaded.Fields["System.Tags"].ShouldBe("backend; api");
        loaded.Fields["System.Description"].ShouldBe("<p>HTML content</p>");
        loaded.Fields["Custom.Team"].ShouldBe("Platform");
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
            var writerRepo = new SqliteWorkItemRepository(writerStore, new WorkItemMapper());

            var seedItem = CreateWorkItem(999, "Task", "Seed Item", "Active");
            await writerRepo.SaveAsync(seedItem);

            // Create a second connection for reading (simulates concurrent access)
            using var readerStore = new SqliteCacheStore($"Data Source={dbPath}");
            var readerRepo = new SqliteWorkItemRepository(readerStore, new WorkItemMapper());

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
            var repo = new SqliteWorkItemRepository(store, new WorkItemMapper());

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

    [Fact]
    public async Task SaveAsync_FieldsRoundTrip_WithPriorityStoryPointsTags()
    {
        var item = CreateWorkItem(1, "User Story", "Field Round-Trip", "Active");
        item.SetField("Microsoft.VSTS.Common.Priority", "2");
        item.SetField("Microsoft.VSTS.Scheduling.StoryPoints", "8");
        item.SetField("System.Tags", "backend; api");

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);
        loaded.ShouldNotBeNull();
        loaded.Fields.Count.ShouldBeGreaterThanOrEqualTo(3);
        loaded.Fields.ShouldContainKey("Microsoft.VSTS.Common.Priority");
        loaded.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        loaded.Fields.ShouldContainKey("Microsoft.VSTS.Scheduling.StoryPoints");
        loaded.Fields["Microsoft.VSTS.Scheduling.StoryPoints"].ShouldBe("8");
        loaded.Fields.ShouldContainKey("System.Tags");
        loaded.Fields["System.Tags"].ShouldBe("backend; api");
    }

    // ═══════════════════════════════════════════════════════════════
    //  DeleteByIdAsync tests (E1-T9, E1-T11)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteByIdAsync_RemovesSingleRow()
    {
        var item1 = CreateWorkItem(1, "Task", "Item 1", "Active");
        var item2 = CreateWorkItem(2, "Task", "Item 2", "Active");
        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);

        await _repo.DeleteByIdAsync(1);

        (await _repo.GetByIdAsync(1)).ShouldBeNull();
        (await _repo.GetByIdAsync(2)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteByIdAsync_NonExistentId_DoesNotThrow()
    {
        await _repo.DeleteByIdAsync(999); // should complete without error
    }

    [Fact]
    public async Task DeleteByIdAsync_RemovesSeedByNegativeId()
    {
        var seed = new WorkItemBuilder(-1, "Delete Me").AsSeed().Build();
        await _repo.SaveAsync(seed);

        (await _repo.GetByIdAsync(seed.Id)).ShouldNotBeNull();
        await _repo.DeleteByIdAsync(seed.Id);
        (await _repo.GetByIdAsync(seed.Id)).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetMinSeedIdAsync tests (E1-T10, E1-T11)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetMinSeedIdAsync_ReturnsNull_WhenNoSeeds()
    {
        var result = await _repo.GetMinSeedIdAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetMinSeedIdAsync_ReturnsNull_WhenOnlyNonSeeds()
    {
        var item = CreateWorkItem(1, "Task", "Regular", "Active");
        await _repo.SaveAsync(item);

        var result = await _repo.GetMinSeedIdAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetMinSeedIdAsync_ReturnsSmallestSeedId()
    {
        var seed1 = new WorkItemBuilder(-11, "Seed 1").AsSeed().Build();
        var seed2 = new WorkItemBuilder(-12, "Seed 2").AsSeed().Build();
        var seed3 = new WorkItemBuilder(-13, "Seed 3").AsSeed().Build();
        await _repo.SaveBatchAsync(new[] { seed1, seed2, seed3 });

        var minId = await _repo.GetMinSeedIdAsync();

        minId.ShouldNotBeNull();
        minId.Value.ShouldBe(new[] { seed1.Id, seed2.Id, seed3.Id }.Min());
    }

    [Fact]
    public async Task GetMinSeedIdAsync_IgnoresNonSeedItems()
    {
        var regular = CreateWorkItem(1, "Task", "Regular", "Active");
        var seed = new WorkItemBuilder(-6, "Seed").AsSeed().Build();
        await _repo.SaveAsync(regular);
        await _repo.SaveAsync(seed);

        var minId = await _repo.GetMinSeedIdAsync();

        minId.ShouldNotBeNull();
        minId.Value.ShouldBe(seed.Id);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetByAreaPathsAsync tests (area mode)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetByAreaPathsAsync_EmptyEntries_ReturnsEmpty()
    {
        var item = CreateWorkItem(1, "Task", "Item", "Active", areaPath: @"Project\Team A");
        await _repo.SaveAsync(item);

        var results = await _repo.GetByAreaPathsAsync(Array.Empty<AreaPathFilter>());

        results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_ExactMatch_ReturnsSingleItem()
    {
        var item1 = CreateWorkItem(1, "Task", "Team A Task", "Active", areaPath: @"Project\Team A");
        var item2 = CreateWorkItem(2, "Task", "Team B Task", "Active", areaPath: @"Project\Team B");
        await _repo.SaveAsync(item1);
        await _repo.SaveAsync(item2);

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team A", IncludeChildren: false),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
        results[0].Title.ShouldBe("Team A Task");
    }

    [Fact]
    public async Task GetByAreaPathsAsync_ExactMatch_CaseInsensitive()
    {
        var item = CreateWorkItem(1, "Task", "Item", "Active", areaPath: @"Project\Team A");
        await _repo.SaveAsync(item);

        var filters = new List<AreaPathFilter>
        {
            new(@"project\team a", IncludeChildren: false),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_UnderSemantics_ReturnsChildPaths()
    {
        var parent = CreateWorkItem(1, "Task", "Parent Area", "Active", areaPath: @"Project\Team A");
        var child = CreateWorkItem(2, "Task", "Child Area", "Active", areaPath: @"Project\Team A\Sub1");
        var grandchild = CreateWorkItem(3, "Task", "Grandchild", "Active", areaPath: @"Project\Team A\Sub1\Deep");
        var unrelated = CreateWorkItem(4, "Task", "Team B", "Active", areaPath: @"Project\Team B");
        await _repo.SaveBatchAsync(new[] { parent, child, grandchild, unrelated });

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team A", IncludeChildren: true),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(3);
        results.Select(r => r.Id).ShouldBe(new[] { 1, 2, 3 }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_UnderSemantics_DoesNotMatchPartialNames()
    {
        // "Team A" filter should NOT match "Team AB" — only "Team A" or "Team A\..."
        var teamA = CreateWorkItem(1, "Task", "Team A", "Active", areaPath: @"Project\Team A");
        var teamAB = CreateWorkItem(2, "Task", "Team AB", "Active", areaPath: @"Project\Team AB");
        await _repo.SaveAsync(teamA);
        await _repo.SaveAsync(teamAB);

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team A", IncludeChildren: true),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(1);
        results[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_MultipleFilters_ReturnsUnion()
    {
        var item1 = CreateWorkItem(1, "Task", "Team A", "Active", areaPath: @"Project\Team A");
        var item2 = CreateWorkItem(2, "Task", "Team B", "Active", areaPath: @"Project\Team B");
        var item3 = CreateWorkItem(3, "Task", "Team C", "Active", areaPath: @"Project\Team C");
        await _repo.SaveBatchAsync(new[] { item1, item2, item3 });

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team A", IncludeChildren: false),
            new(@"Project\Team C", IncludeChildren: false),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(2);
        results.Select(r => r.Id).ShouldBe(new[] { 1, 3 }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_MixedExactAndUnder_ReturnsCorrectResults()
    {
        var itemA = CreateWorkItem(1, "Task", "Team A Root", "Active", areaPath: @"Project\Team A");
        var itemAChild = CreateWorkItem(2, "Task", "Team A Child", "Active", areaPath: @"Project\Team A\Sub");
        var itemB = CreateWorkItem(3, "Task", "Team B Exact", "Active", areaPath: @"Project\Team B");
        var itemBChild = CreateWorkItem(4, "Task", "Team B Child", "Active", areaPath: @"Project\Team B\Sub");
        await _repo.SaveBatchAsync(new[] { itemA, itemAChild, itemB, itemBChild });

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team A", IncludeChildren: true),  // should get 1 + 2
            new(@"Project\Team B", IncludeChildren: false),  // should get only 3
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(3);
        results.Select(r => r.Id).ShouldBe(new[] { 1, 2, 3 }, ignoreOrder: true);
    }

    [Fact]
    public async Task GetByAreaPathsAsync_NoMatches_ReturnsEmpty()
    {
        var item = CreateWorkItem(1, "Task", "Item", "Active", areaPath: @"Project\Team A");
        await _repo.SaveAsync(item);

        var filters = new List<AreaPathFilter>
        {
            new(@"Project\Team Z", IncludeChildren: true),
        };
        var results = await _repo.GetByAreaPathsAsync(filters);

        results.Count.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Snapshot intermediate step verification
    //  Verify MapRowToSnapshot() produces a snapshot with all
    //  expected field values after save.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSnapshotByIdAsync_ProducesSnapshotWithAllExpectedFields()
    {
        var seedCreatedAt = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.UserStory,
            Title = "Snapshot Verification Item",
            State = "Resolved",
            ParentId = 10,
            AssignedTo = "Alice",
            IterationPath = IterationPath.Parse(@"Project\Sprint3").Value,
            AreaPath = AreaPath.Parse(@"Project\Backend").Value,
            IsSeed = true,
            SeedCreatedAt = seedCreatedAt,
        };
        item.MarkSynced(7);
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["System.Tags"] = "api; backend",
            ["Custom.Team"] = "Platform",
        });
        item.SetDirty();

        await _repo.SaveAsync(item);

        var snapshot = await _repo.GetSnapshotByIdAsync(42);
        snapshot.ShouldNotBeNull();

        snapshot.Id.ShouldBe(42);
        snapshot.TypeName.ShouldBe("User Story");
        snapshot.Title.ShouldBe("Snapshot Verification Item");
        snapshot.State.ShouldBe("Resolved");
        snapshot.ParentId.ShouldBe(10);
        snapshot.AssignedTo.ShouldBe("Alice");
        snapshot.IterationPath.ShouldBe(@"Project\Sprint3");
        snapshot.AreaPath.ShouldBe(@"Project\Backend");
        snapshot.Revision.ShouldBe(7);
        snapshot.IsSeed.ShouldBeTrue();
        snapshot.SeedCreatedAt.ShouldNotBeNull();
        snapshot.SeedCreatedAt!.Value.Year.ShouldBe(2025);
        snapshot.SeedCreatedAt.Value.Month.ShouldBe(6);
        snapshot.SeedCreatedAt.Value.Day.ShouldBe(15);
        snapshot.LastSyncedAt.ShouldNotBeNull();
        snapshot.IsDirty.ShouldBeTrue();

        snapshot.Fields.Count.ShouldBe(3);
        snapshot.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("1");
        snapshot.Fields["System.Tags"].ShouldBe("api; backend");
        snapshot.Fields["Custom.Team"].ShouldBe("Platform");
    }

    [Fact]
    public async Task GetSnapshotByIdAsync_NullableFieldsPreservedAsNull()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Nullable Test",
            State = "New",
        };

        await _repo.SaveAsync(item);

        var snapshot = await _repo.GetSnapshotByIdAsync(1);
        snapshot.ShouldNotBeNull();

        snapshot.ParentId.ShouldBeNull();
        snapshot.AssignedTo.ShouldBeNull();
        snapshot.IsSeed.ShouldBeFalse();
        snapshot.SeedCreatedAt.ShouldBeNull();
        snapshot.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task GetSnapshotByIdAsync_ReturnsNull_WhenNotFound()
    {
        var snapshot = await _repo.GetSnapshotByIdAsync(999);
        snapshot.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Save-read round-trip: comprehensive property verification
    //  Save a WorkItem via SaveAsync, read it back via GetByIdAsync,
    //  assert ALL properties are identical.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveAndGetById_ComprehensiveRoundTrip_AllPropertiesPreserved()
    {
        var seedCreatedAt = new DateTimeOffset(2025, 3, 20, 14, 0, 0, TimeSpan.Zero);
        var original = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Feature,
            Title = "Comprehensive Round-Trip Item",
            State = "Active",
            ParentId = 50,
            AssignedTo = "Bob",
            IterationPath = IterationPath.Parse(@"MyProject\Sprint 5").Value,
            AreaPath = AreaPath.Parse(@"MyProject\Frontend").Value,
            IsSeed = true,
            SeedCreatedAt = seedCreatedAt,
        };
        original.MarkSynced(12);
        original.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            ["System.Description"] = "<p>Some HTML</p>",
            ["System.Tags"] = "frontend; ux",
            ["Custom.NullableField"] = null,
        });

        await _repo.SaveAsync(original);

        var loaded = await _repo.GetByIdAsync(100);
        loaded.ShouldNotBeNull();

        // Identity
        loaded.Id.ShouldBe(original.Id);
        loaded.Type.ShouldBe(original.Type);
        loaded.Title.ShouldBe(original.Title);
        loaded.State.ShouldBe(original.State);

        // Relationships & assignment
        loaded.ParentId.ShouldBe(original.ParentId);
        loaded.AssignedTo.ShouldBe(original.AssignedTo);

        // Paths
        loaded.IterationPath.Value.ShouldBe(original.IterationPath.Value);
        loaded.AreaPath.Value.ShouldBe(original.AreaPath.Value);

        // Revision
        loaded.Revision.ShouldBe(original.Revision);

        // Seed
        loaded.IsSeed.ShouldBe(original.IsSeed);
        loaded.SeedCreatedAt.ShouldNotBeNull();
        loaded.SeedCreatedAt!.Value.Year.ShouldBe(seedCreatedAt.Year);
        loaded.SeedCreatedAt.Value.Month.ShouldBe(seedCreatedAt.Month);
        loaded.SeedCreatedAt.Value.Day.ShouldBe(seedCreatedAt.Day);

        // Dirty flag should not be set (MarkSynced clears it, and no mutations after)
        loaded.IsDirty.ShouldBeFalse();

        // Fields
        loaded.Fields.Count.ShouldBe(5);
        loaded.Fields["Microsoft.VSTS.Common.Priority"].ShouldBe("2");
        loaded.Fields["Microsoft.VSTS.Scheduling.StoryPoints"].ShouldBe("5");
        loaded.Fields["System.Description"].ShouldBe("<p>Some HTML</p>");
        loaded.Fields["System.Tags"].ShouldBe("frontend; ux");
        loaded.Fields.ShouldContainKey("Custom.NullableField");
    }

    [Fact]
    public async Task SaveAndGetById_DirtyItemRoundTrip_PreservesDirtyFlag()
    {
        var original = CreateWorkItem(200, "Bug", "Dirty Round-Trip", "Active",
            parentId: 99, assignedTo: "Carol");
        original.UpdateField("System.Title", "Changed Title");

        original.IsDirty.ShouldBeTrue();

        await _repo.SaveAsync(original);

        var loaded = await _repo.GetByIdAsync(200);
        loaded.ShouldNotBeNull();
        loaded.IsDirty.ShouldBeTrue();
        loaded.Id.ShouldBe(200);
        loaded.Type.ShouldBe(WorkItemType.Bug);
        loaded.State.ShouldBe("Active");
        loaded.ParentId.ShouldBe(99);
        loaded.AssignedTo.ShouldBe("Carol");
    }

    [Fact]
    public async Task SaveAndGetById_SeedWithNegativeId_RoundTrips()
    {
        var seed = new WorkItemBuilder(-5, "Seed Round-Trip")
            .AsSeed()
            .AsUserStory()
            .InState("New")
            .WithParent(10)
            .AssignedTo("Dave")
            .WithIterationPath(@"Project\Sprint1")
            .WithAreaPath(@"Project\Area")
            .WithField("System.Tags", "seed; test")
            .Build();

        await _repo.SaveAsync(seed);

        var loaded = await _repo.GetByIdAsync(-5);
        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(-5);
        loaded.IsSeed.ShouldBeTrue();
        loaded.Title.ShouldBe("Seed Round-Trip");
        loaded.Type.ShouldBe(WorkItemType.UserStory);
        loaded.ParentId.ShouldBe(10);
        loaded.AssignedTo.ShouldBe("Dave");
        loaded.Fields.ShouldContainKey("System.Tags");
        loaded.Fields["System.Tags"].ShouldBe("seed; test");
    }

    [Fact]
    public async Task SaveAndGetById_EmptyFields_RoundTripsAsEmptyDictionary()
    {
        var item = CreateWorkItem(300, "Task", "No Fields", "New");

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(300);
        loaded.ShouldNotBeNull();
        loaded.Fields.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SaveAndGetById_CustomWorkItemType_RoundTripsTypeName()
    {
        var customType = WorkItemType.Parse("Deliverable").Value;
        var item = new WorkItem
        {
            Id = 400,
            Type = customType,
            Title = "Custom Type Round-Trip",
            State = "Active",
        };

        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(400);
        loaded.ShouldNotBeNull();
        loaded.Type.Value.ShouldBe("Deliverable");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Write path verification: SaveWorkItem reads WorkItem
    //  properties directly (not from snapshot).
    //  Verified by comparing snapshot field values with original
    //  WorkItem properties — the snapshot must match what was on
    //  the WorkItem at save time.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveWorkItem_WritesDirectWorkItemProperties_SnapshotMatchesOriginal()
    {
        var item = new WorkItem
        {
            Id = 500,
            Type = WorkItemType.Epic,
            Title = "Write Path Verification",
            State = "Closed",
            ParentId = 42,
            AssignedTo = "Eve",
            IterationPath = IterationPath.Parse(@"Org\Release 2").Value,
            AreaPath = AreaPath.Parse(@"Org\Platform").Value,
            IsSeed = false,
        };
        item.MarkSynced(3);
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.CreatedBy"] = "Admin",
        });

        await _repo.SaveAsync(item);

        // Read raw snapshot to verify SaveWorkItem wrote the WorkItem's direct properties
        var snapshot = await _repo.GetSnapshotByIdAsync(500);
        snapshot.ShouldNotBeNull();

        // Each snapshot field should match the WorkItem property used in SaveWorkItem
        snapshot.Id.ShouldBe(item.Id);
        snapshot.TypeName.ShouldBe(item.Type.ToString());
        snapshot.Title.ShouldBe(item.Title);
        snapshot.State.ShouldBe(item.State);
        snapshot.ParentId.ShouldBe(item.ParentId);
        snapshot.AssignedTo.ShouldBe(item.AssignedTo);
        snapshot.IterationPath.ShouldBe(item.IterationPath.Value);
        snapshot.AreaPath.ShouldBe(item.AreaPath.Value);
        snapshot.Revision.ShouldBe(item.Revision);
        snapshot.IsSeed.ShouldBe(item.IsSeed);
        snapshot.IsDirty.ShouldBe(item.IsDirty);

        // Fields written from item.Fields
        snapshot.Fields.Count.ShouldBe(1);
        snapshot.Fields["System.CreatedBy"].ShouldBe("Admin");
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
