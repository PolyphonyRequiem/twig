using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for WorkItem.LastSyncedAt mapping in SqliteWorkItemRepository.
/// Verifies that the last_synced_at column is correctly read into the domain property.
/// </summary>
public class SqliteWorkItemRepositoryLastSyncedAtTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkItemRepository _repo;

    public SqliteWorkItemRepositoryLastSyncedAtTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task SaveAndLoad_LastSyncedAt_IsPopulated()
    {
        var item = CreateWorkItem(1, "Task", "Test Item", "Active");
        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(1);

        loaded.ShouldNotBeNull();
        loaded.LastSyncedAt.ShouldNotBeNull();
        // SaveWorkItem writes DateTimeOffset.UtcNow — it should be very recent
        (DateTimeOffset.UtcNow - loaded.LastSyncedAt!.Value).TotalSeconds.ShouldBeLessThan(5);
    }

    [Fact]
    public async Task SaveAndLoad_LastSyncedAt_RoundTripsWithCorrectFormat()
    {
        var item = CreateWorkItem(2, "Bug", "Bug Item", "New");
        await _repo.SaveAsync(item);

        var loaded = await _repo.GetByIdAsync(2);

        loaded.ShouldNotBeNull();
        loaded.LastSyncedAt.ShouldNotBeNull();
        // Verify it's a valid DateTimeOffset with UTC offset
        loaded.LastSyncedAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task SaveAndLoad_LastSyncedAt_UpdatesOnSubsequentSave()
    {
        var item = CreateWorkItem(3, "Task", "Item", "Active");
        await _repo.SaveAsync(item);

        var loaded1 = await _repo.GetByIdAsync(3);
        var firstSync = loaded1!.LastSyncedAt;

        // Small delay to ensure different timestamp
        await Task.Delay(50);

        // Re-save
        await _repo.SaveAsync(item);
        var loaded2 = await _repo.GetByIdAsync(3);

        loaded2!.LastSyncedAt.ShouldNotBeNull();
        loaded2.LastSyncedAt!.Value.ShouldBeGreaterThanOrEqualTo(firstSync!.Value);
    }

    [Fact]
    public async Task SaveBatchAndLoad_LastSyncedAt_IsPopulatedForAllItems()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Task", "Item 10", "Active"),
            CreateWorkItem(11, "Task", "Item 11", "Active"),
        };

        await _repo.SaveBatchAsync(items);

        var loaded10 = await _repo.GetByIdAsync(10);
        var loaded11 = await _repo.GetByIdAsync(11);

        loaded10.ShouldNotBeNull();
        loaded10.LastSyncedAt.ShouldNotBeNull();
        loaded11.ShouldNotBeNull();
        loaded11.LastSyncedAt.ShouldNotBeNull();
    }

    private static WorkItem CreateWorkItem(
        int id, string type, string title, string state)
    {
        var typeResult = WorkItemType.Parse(type);
        var iterResult = IterationPath.Parse(@"Project\Sprint1");
        var areaResult = AreaPath.Parse(@"Project\Area");

        return new WorkItem
        {
            Id = id,
            Type = typeResult.Value,
            Title = title,
            State = state,
            IterationPath = iterResult.Value,
            AreaPath = areaResult.Value,
        };
    }
}
