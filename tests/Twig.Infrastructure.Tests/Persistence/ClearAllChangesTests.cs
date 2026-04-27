using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for <see cref="SqlitePendingChangeStore.ClearAllChangesAsync"/>.
/// Verifies bulk deletion of non-seed pending changes and orphan cleanup.
/// </summary>
public sealed class ClearAllChangesTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqlitePendingChangeStore _changeStore;
    private readonly SqliteWorkItemRepository _repo;

    public ClearAllChangesTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _changeStore = new SqlitePendingChangeStore(_store);
        _repo = new SqliteWorkItemRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task EmptyStore_ReturnsZero()
    {
        var count = await _changeStore.ClearAllChangesAsync();
        count.ShouldBe(0);
    }

    [Fact]
    public async Task DeletesChangesForNonSeedItems()
    {
        await SaveWorkItem(1, isSeed: false);
        await SaveWorkItem(2, isSeed: false);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(2, "add_note", null, null, "Note");

        var count = await _changeStore.ClearAllChangesAsync();

        count.ShouldBe(2);
        (await _changeStore.GetChangesAsync(1)).ShouldBeEmpty();
        (await _changeStore.GetChangesAsync(2)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PreservesSeedItemChanges()
    {
        var seed = new WorkItemBuilder(-101, "Seed Item").AsSeed().Build();
        await _repo.SaveAsync(seed);
        await _changeStore.AddChangeAsync(seed.Id, "set_field", "System.Title", "Old", "New");

        var count = await _changeStore.ClearAllChangesAsync();

        count.ShouldBe(0);
        (await _changeStore.GetChangesAsync(seed.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task MixedSeedAndNonSeed_OnlyDeletesNonSeed()
    {
        await SaveWorkItem(1, isSeed: false);
        var seed = new WorkItemBuilder(-201, "Seed").AsSeed().Build();
        await _repo.SaveAsync(seed);

        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");
        await _changeStore.AddChangeAsync(1, "add_note", null, null, "Note");
        await _changeStore.AddChangeAsync(seed.Id, "set_field", "System.Title", "X", "Y");

        var count = await _changeStore.ClearAllChangesAsync();

        count.ShouldBe(2);
        (await _changeStore.GetChangesAsync(1)).ShouldBeEmpty();
        (await _changeStore.GetChangesAsync(seed.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task CleansUpOrphanedRows()
    {
        // Create a work item, add a change, then delete the item to leave an orphan
        await SaveWorkItem(99, isSeed: false);
        await _changeStore.AddChangeAsync(99, "set_field", "System.Title", "A", "B");
        DeleteWorkItemDirect(99);

        var count = await _changeStore.ClearAllChangesAsync();

        count.ShouldBe(1);
        (await _changeStore.GetChangesAsync(99)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Idempotent_SecondCallReturnsZero()
    {
        await SaveWorkItem(1, isSeed: false);
        await _changeStore.AddChangeAsync(1, "set_field", "System.Title", "A", "B");

        var first = await _changeStore.ClearAllChangesAsync();
        var second = await _changeStore.ClearAllChangesAsync();

        first.ShouldBe(1);
        second.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private async Task SaveWorkItem(int id, bool isSeed)
    {
        var item = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse("Task").Value,
            Title = $"Work Item {id}",
            State = "Active",
            IsSeed = isSeed,
            IterationPath = IterationPath.Parse(@"Project\Sprint1").Value,
            AreaPath = AreaPath.Parse(@"Project\Area").Value,
        };
        await _repo.SaveAsync(item);
    }

    private void DeleteWorkItemDirect(int id)
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = OFF; DELETE FROM work_items WHERE id = @id; PRAGMA foreign_keys = ON;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
}
