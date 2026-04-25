using Shouldly;
using Twig.Domain.Enums;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqliteTrackingRepository"/>.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteTrackingRepositoryTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteTrackingRepository _repo;

    public SqliteTrackingRepositoryTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _repo = new SqliteTrackingRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    // --- GetAllTrackedAsync ---

    [Fact]
    public async Task GetAllTrackedAsync_Empty_ReturnsEmptyList()
    {
        var items = await _repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllTrackedAsync_ReturnsItemsOrderedByTimestamp()
    {
        await _repo.UpsertTrackedAsync(10, TrackingMode.Single);
        await _repo.UpsertTrackedAsync(20, TrackingMode.Tree);

        var items = await _repo.GetAllTrackedAsync();

        items.Count.ShouldBe(2);
        items[0].WorkItemId.ShouldBe(10);
        items[0].Mode.ShouldBe(TrackingMode.Single);
        items[1].WorkItemId.ShouldBe(20);
        items[1].Mode.ShouldBe(TrackingMode.Tree);
    }

    // --- GetTrackedByWorkItemIdAsync ---

    [Fact]
    public async Task GetTrackedByWorkItemIdAsync_NotFound_ReturnsNull()
    {
        var item = await _repo.GetTrackedByWorkItemIdAsync(999);
        item.ShouldBeNull();
    }

    [Fact]
    public async Task GetTrackedByWorkItemIdAsync_Found_ReturnsItem()
    {
        await _repo.UpsertTrackedAsync(42, TrackingMode.Tree);

        var item = await _repo.GetTrackedByWorkItemIdAsync(42);

        item.ShouldNotBeNull();
        item.WorkItemId.ShouldBe(42);
        item.Mode.ShouldBe(TrackingMode.Tree);
        item.TrackedAt.ShouldNotBe(default);
    }

    // --- UpsertTrackedAsync ---

    [Fact]
    public async Task UpsertTrackedAsync_Insert_CreatesNewItem()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);

        var items = await _repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].WorkItemId.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Single);
    }

    [Fact]
    public async Task UpsertTrackedAsync_Update_OverwritesExistingMode()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);
        var before = await _repo.GetTrackedByWorkItemIdAsync(1);

        await _repo.UpsertTrackedAsync(1, TrackingMode.Tree);
        var after = await _repo.GetTrackedByWorkItemIdAsync(1);

        var items = await _repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Tree);

        // ON CONFLICT preserves original created_at
        after!.TrackedAt.ShouldBe(before!.TrackedAt);
    }

    // --- RemoveTrackedAsync ---

    [Fact]
    public async Task RemoveTrackedAsync_ExistingItem_RemovesIt()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await _repo.RemoveTrackedAsync(1);

        var items = await _repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveTrackedAsync_NonExistent_NoOp()
    {
        // Should not throw
        await _repo.RemoveTrackedAsync(999);
        var items = await _repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    // --- RemoveTrackedBatchAsync ---

    [Fact]
    public async Task RemoveTrackedBatchAsync_EmptyList_NoOp()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await _repo.RemoveTrackedBatchAsync([]);

        var items = await _repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveTrackedBatchAsync_RemovesOnlySpecifiedItems()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);
        await _repo.UpsertTrackedAsync(2, TrackingMode.Tree);
        await _repo.UpsertTrackedAsync(3, TrackingMode.Single);

        await _repo.RemoveTrackedBatchAsync([1, 3]);

        var items = await _repo.GetAllTrackedAsync();
        items.Count.ShouldBe(1);
        items[0].WorkItemId.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveTrackedBatchAsync_MixedExistingAndNonExistent_RemovesExisting()
    {
        await _repo.UpsertTrackedAsync(1, TrackingMode.Single);

        await _repo.RemoveTrackedBatchAsync([1, 999]);

        var items = await _repo.GetAllTrackedAsync();
        items.ShouldBeEmpty();
    }

    // --- GetAllExcludedAsync ---

    [Fact]
    public async Task GetAllExcludedAsync_Empty_ReturnsEmptyList()
    {
        var items = await _repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllExcludedAsync_ReturnsItemsOrderedByTimestamp()
    {
        await _repo.AddExcludedAsync(10);
        await _repo.AddExcludedAsync(20);

        var items = await _repo.GetAllExcludedAsync();

        items.Count.ShouldBe(2);
        items[0].WorkItemId.ShouldBe(10);
        items[0].ExcludedAt.ShouldNotBe(default);
        items[1].WorkItemId.ShouldBe(20);
    }

    // --- AddExcludedAsync ---

    [Fact]
    public async Task AddExcludedAsync_Idempotent_NoDuplicate()
    {
        await _repo.AddExcludedAsync(1);
        await _repo.AddExcludedAsync(1);

        var items = await _repo.GetAllExcludedAsync();
        items.Count.ShouldBe(1);
    }

    // --- RemoveExcludedAsync ---

    [Fact]
    public async Task RemoveExcludedAsync_ExistingItem_RemovesIt()
    {
        await _repo.AddExcludedAsync(1);
        await _repo.RemoveExcludedAsync(1);

        var items = await _repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveExcludedAsync_NonExistent_NoOp()
    {
        await _repo.RemoveExcludedAsync(999);
        var items = await _repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    // --- ClearAllExcludedAsync ---

    [Fact]
    public async Task ClearAllExcludedAsync_RemovesAll()
    {
        await _repo.AddExcludedAsync(1);
        await _repo.AddExcludedAsync(2);
        await _repo.AddExcludedAsync(3);

        await _repo.ClearAllExcludedAsync();

        var items = await _repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClearAllExcludedAsync_EmptyTable_NoOp()
    {
        await _repo.ClearAllExcludedAsync();
        var items = await _repo.GetAllExcludedAsync();
        items.ShouldBeEmpty();
    }

    // --- Cross-concern: tracked and excluded are independent ---

    [Fact]
    public async Task TrackedAndExcluded_AreIndependent()
    {
        await _repo.UpsertTrackedAsync(42, TrackingMode.Single);
        await _repo.AddExcludedAsync(42);

        var tracked = await _repo.GetAllTrackedAsync();
        var excluded = await _repo.GetAllExcludedAsync();

        tracked.Count.ShouldBe(1);
        excluded.Count.ShouldBe(1);

        // Removing from tracked doesn't affect excluded
        await _repo.RemoveTrackedAsync(42);
        tracked = await _repo.GetAllTrackedAsync();
        excluded = await _repo.GetAllExcludedAsync();
        tracked.ShouldBeEmpty();
        excluded.Count.ShouldBe(1);
    }
}
