using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqliteContextStore: set/get active ID, arbitrary keys, overwrite, missing returns null.
/// </summary>
public class SqliteContextStoreTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteContextStore _contextStore;

    public SqliteContextStoreTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _contextStore = new SqliteContextStore(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetActiveWorkItemIdAsync_ReturnsNull_WhenNotSet()
    {
        var result = await _contextStore.GetActiveWorkItemIdAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetAndGetActiveWorkItemId_RoundTrip()
    {
        await _contextStore.SetActiveWorkItemIdAsync(42);
        var result = await _contextStore.GetActiveWorkItemIdAsync();
        result.ShouldBe(42);
    }

    [Fact]
    public async Task SetActiveWorkItemId_Overwrites()
    {
        await _contextStore.SetActiveWorkItemIdAsync(42);
        await _contextStore.SetActiveWorkItemIdAsync(99);
        var result = await _contextStore.GetActiveWorkItemIdAsync();
        result.ShouldBe(99);
    }

    [Fact]
    public async Task GetValueAsync_ReturnsNull_WhenMissing()
    {
        var result = await _contextStore.GetValueAsync("nonexistent_key");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SetAndGetValue_ArbitraryKey()
    {
        await _contextStore.SetValueAsync("current_iteration", @"Project\Sprint1");
        var result = await _contextStore.GetValueAsync("current_iteration");
        result.ShouldBe(@"Project\Sprint1");
    }

    [Fact]
    public async Task SetValue_OverwritesExisting()
    {
        await _contextStore.SetValueAsync("my_key", "value1");
        await _contextStore.SetValueAsync("my_key", "value2");
        var result = await _contextStore.GetValueAsync("my_key");
        result.ShouldBe("value2");
    }

    [Fact]
    public async Task ClearActiveWorkItemIdAsync_RemovesActiveId()
    {
        await _contextStore.SetActiveWorkItemIdAsync(42);
        await _contextStore.ClearActiveWorkItemIdAsync();
        var result = await _contextStore.GetActiveWorkItemIdAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task ClearActiveWorkItemIdAsync_WhenNotSet_DoesNotThrow()
    {
        await _contextStore.ClearActiveWorkItemIdAsync();
        var result = await _contextStore.GetActiveWorkItemIdAsync();
        result.ShouldBeNull();
    }
}
