using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqlitePendingChangeStore: add, retrieve in order, clear by item, dirty IDs, empty results.
/// </summary>
public class SqlitePendingChangeStoreTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteWorkItemRepository _workItemRepo;

    public SqlitePendingChangeStoreTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _workItemRepo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetChangesAsync_ReturnsEmpty_WhenNone()
    {
        var changes = await _changeStore.GetChangesAsync(1);
        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAndGetChanges_RoundTrip()
    {
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "Old Title", "New Title");

        var changes = await _changeStore.GetChangesAsync(1);
        changes.Count.ShouldBe(1);
        changes[0].WorkItemId.ShouldBe(1);
        changes[0].ChangeType.ShouldBe("set_field");
        changes[0].FieldName.ShouldBe("System.Title");
        changes[0].OldValue.ShouldBe("Old Title");
        changes[0].NewValue.ShouldBe("New Title");
    }

    [Fact]
    public async Task GetChangesAsync_ReturnsInOrder()
    {
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "set_field", "System.State", "New", "Active");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "A note");

        var changes = await _changeStore.GetChangesAsync(1);
        changes.Count.ShouldBe(3);
        changes[0].FieldName.ShouldBe("System.Title");
        changes[1].FieldName.ShouldBe("System.State");
        changes[2].ChangeType.ShouldBe("add_note");
    }

    [Fact]
    public async Task ClearChangesAsync_RemovesOnlyForSpecifiedItem()
    {
        await InsertWorkItemAsync(1);
        await InsertWorkItemAsync(2);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(2, "set_field", "System.Title", "C", "D");

        await _changeStore.ClearChangesAsync(1);

        var changes1 = await _changeStore.GetChangesAsync(1);
        var changes2 = await _changeStore.GetChangesAsync(2);

        changes1.ShouldBeEmpty();
        changes2.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetDirtyItemIdsAsync_ReturnsDistinctIds()
    {
        await InsertWorkItemAsync(1);
        await InsertWorkItemAsync(3);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "set_field", "System.State", "New", "Active");
        await _changeStore.AddChangeAsync(3, "add_note", null, null, "Note");

        var ids = await _changeStore.GetDirtyItemIdsAsync();
        ids.Count.ShouldBe(2);
        ids.ShouldContain(1);
        ids.ShouldContain(3);
    }

    [Fact]
    public async Task GetDirtyItemIdsAsync_ReturnsEmpty_WhenNoChanges()
    {
        var ids = await _changeStore.GetDirtyItemIdsAsync();
        ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddChangeAsync_WithNullFields()
    {
        await InsertWorkItemAsync(1);
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note text");

        var changes = await _changeStore.GetChangesAsync(1);
        changes.Count.ShouldBe(1);
        changes[0].FieldName.ShouldBeNull();
        changes[0].OldValue.ShouldBeNull();
        changes[0].NewValue.ShouldBe("Note text");
    }

    [Fact]
    public async Task AddChangesBatchAsync_SecondRowFailureRollsBackAfterReopen_CompleteRetryPersists()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twig-pending-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "twig.db"),
            Pooling = false,
        }.ToString();
        PendingChangeRecord[] existing = [new(1, "set_field", "System.Title", "Earlier title", "Staged title")];
        PendingChangeRecord[] unrelated = [new(2, "add_note", null, null, "Keep unrelated note")];
        (string ChangeType, string? FieldName, string? OldValue, string? NewValue)[] batch =
        [
            ("set_field", "System.Title", "Staged title", "Batch title"),
            ("set_field", "System.State", "New", "Active"),
            ("add_note", null, null, "Complete batch note"),
        ];

        try
        {
            using (var store = new SqliteCacheStore(connection))
            {
                var items = new SqliteWorkItemRepository(store, new WorkItemMapper());
                await items.SaveAsync(new WorkItem { Id = 1, Type = WorkItemType.Task, Title = "Target item", State = "New" });
                await items.SaveAsync(new WorkItem { Id = 2, Type = WorkItemType.Task, Title = "Unrelated item", State = "Active" });
                var changes = new SqlitePendingChangeStore(store);
                foreach (var change in existing.Concat(unrelated))
                    await changes.AddChangeAsync(change.WorkItemId, change.ChangeType, change.FieldName, change.OldValue, change.NewValue);

                using (var trigger = store.GetConnection().CreateCommand())
                {
                    trigger.CommandText = """
                        CREATE TRIGGER pending.refuse_second_batch_row BEFORE INSERT ON pending_changes
                        WHEN NEW.work_item_id = 1 AND NEW.field_name = 'System.State'
                            AND EXISTS (SELECT 1 FROM pending_changes
                                WHERE work_item_id = 1 AND field_name = 'System.Title' AND new_value = 'Batch title')
                        BEGIN SELECT RAISE(ABORT, 'Injected second batch row failure'); END;
                        """;
                    await trigger.ExecuteNonQueryAsync();
                }

                var failure = await Should.ThrowAsync<SqliteException>(() => changes.AddChangesBatchAsync(1, batch));
                failure.SqliteErrorCode.ShouldBe(19);
                failure.Message.ShouldContain("Injected second batch row failure");
            }

            using (var reopened = new SqliteCacheStore(connection))
            {
                var changes = new SqlitePendingChangeStore(reopened);
                (await changes.GetChangesAsync(1)).ShouldBe(existing);
                (await changes.GetChangesAsync(2)).ShouldBe(unrelated);
                (await changes.GetDirtyItemIdsAsync()).ShouldBe([1, 2]);
                var items = new SqliteWorkItemRepository(reopened, new WorkItemMapper());
                (await items.GetByIdAsync(1))!.Title.ShouldBe("Target item");
                (await items.GetByIdAsync(1))!.State.ShouldBe("New");
                (await items.GetByIdAsync(2))!.Title.ShouldBe("Unrelated item");
                (await items.GetByIdAsync(2))!.State.ShouldBe("Active");

                using (var trigger = reopened.GetConnection().CreateCommand())
                {
                    trigger.CommandText = "DROP TRIGGER pending.refuse_second_batch_row;";
                    await trigger.ExecuteNonQueryAsync();
                }

                await changes.AddChangesBatchAsync(1, batch);
            }

            using (var reopened = new SqliteCacheStore(connection))
            {
                var changes = new SqlitePendingChangeStore(reopened);
                (await changes.GetChangesAsync(1)).ShouldBe(existing.Concat(batch.Select(change =>
                    new PendingChangeRecord(1, change.ChangeType, change.FieldName, change.OldValue, change.NewValue))).ToArray());
                (await changes.GetChangesAsync(2)).ShouldBe(unrelated);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task InsertWorkItemAsync(int id)
    {
        var typeResult = WorkItemType.Parse("Task");
        var iterResult = IterationPath.Parse(@"Project\Sprint1");
        var areaResult = AreaPath.Parse(@"Project\Area");
        var item = new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = $"Work Item {id}",
            State = "Active",
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
        await _workItemRepo.SaveAsync(item);
    }
}
