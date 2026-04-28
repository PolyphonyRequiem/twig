using Shouldly;
using Twig.Domain.Aggregates;
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
