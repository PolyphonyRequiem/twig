using Shouldly;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public class SqliteWorkspaceModeStoreTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteWorkspaceModeStore _modeStore;

    public SqliteWorkspaceModeStoreTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _modeStore = new SqliteWorkspaceModeStore(_store);
    }

    public void Dispose() => _store.Dispose();

    // --- Active Mode ---

    [Fact]
    public async Task GetActiveModeAsync_DefaultsToSprint()
    {
        var mode = await _modeStore.GetActiveModeAsync();
        mode.ShouldBe(WorkspaceMode.Sprint);
    }

    [Fact]
    public async Task SetAndGetActiveMode_RoundTrip()
    {
        await _modeStore.SetActiveModeAsync(WorkspaceMode.Area);
        var mode = await _modeStore.GetActiveModeAsync();
        mode.ShouldBe(WorkspaceMode.Area);
    }

    [Fact]
    public async Task SetActiveMode_Overwrites()
    {
        await _modeStore.SetActiveModeAsync(WorkspaceMode.Area);
        await _modeStore.SetActiveModeAsync(WorkspaceMode.Recent);
        var mode = await _modeStore.GetActiveModeAsync();
        mode.ShouldBe(WorkspaceMode.Recent);
    }

    // --- Tracked Items ---

    [Fact]
    public async Task GetTrackedItemsAsync_Empty_ReturnsEmptyList()
    {
        var items = await _modeStore.GetTrackedItemsAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAndGetTrackedItem_RoundTrip()
    {
        await _modeStore.AddTrackedItemAsync(42, TrackingMode.Single);
        var items = await _modeStore.GetTrackedItemsAsync();
        items.Count.ShouldBe(1);
        items[0].WorkItemId.ShouldBe(42);
        items[0].Mode.ShouldBe(TrackingMode.Single);
    }

    [Fact]
    public async Task AddTrackedItem_TreeMode()
    {
        await _modeStore.AddTrackedItemAsync(99, TrackingMode.Tree);
        var items = await _modeStore.GetTrackedItemsAsync();
        items.Count.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Tree);
    }

    [Fact]
    public async Task AddTrackedItem_DuplicateId_Overwrites()
    {
        await _modeStore.AddTrackedItemAsync(42, TrackingMode.Single);
        await _modeStore.AddTrackedItemAsync(42, TrackingMode.Tree);
        var items = await _modeStore.GetTrackedItemsAsync();
        items.Count.ShouldBe(1);
        items[0].Mode.ShouldBe(TrackingMode.Tree);
    }

    [Fact]
    public async Task RemoveTrackedItem_Removes()
    {
        await _modeStore.AddTrackedItemAsync(42, TrackingMode.Single);
        await _modeStore.RemoveTrackedItemAsync(42);
        var items = await _modeStore.GetTrackedItemsAsync();
        items.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveTrackedItem_NonExistent_DoesNotThrow()
    {
        await _modeStore.RemoveTrackedItemAsync(999);
        var items = await _modeStore.GetTrackedItemsAsync();
        items.ShouldBeEmpty();
    }

    // --- Excluded Items ---

    [Fact]
    public async Task GetExcludedItemIdsAsync_Empty_ReturnsEmptyList()
    {
        var ids = await _modeStore.GetExcludedItemIdsAsync();
        ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAndGetExcludedItem_RoundTrip()
    {
        await _modeStore.AddExcludedItemAsync(10);
        var ids = await _modeStore.GetExcludedItemIdsAsync();
        ids.Count.ShouldBe(1);
        ids[0].ShouldBe(10);
    }

    [Fact]
    public async Task AddExcludedItem_DuplicateId_NoError()
    {
        await _modeStore.AddExcludedItemAsync(10);
        await _modeStore.AddExcludedItemAsync(10);
        var ids = await _modeStore.GetExcludedItemIdsAsync();
        ids.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RemoveExcludedItem_Removes()
    {
        await _modeStore.AddExcludedItemAsync(10);
        await _modeStore.RemoveExcludedItemAsync(10);
        var ids = await _modeStore.GetExcludedItemIdsAsync();
        ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveExcludedItem_NonExistent_DoesNotThrow()
    {
        await _modeStore.RemoveExcludedItemAsync(999);
    }

    // --- Sprint Iterations ---

    [Fact]
    public async Task GetSprintIterationsAsync_Empty_ReturnsEmptyList()
    {
        var entries = await _modeStore.GetSprintIterationsAsync();
        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetAndGetSprintIterations_RoundTrip()
    {
        var entries = new List<SprintIterationEntry>
        {
            new("@CurrentIteration", "relative"),
            new(@"Project\Sprint 1", "absolute")
        };

        await _modeStore.SetSprintIterationsAsync(entries);
        var result = await _modeStore.GetSprintIterationsAsync();

        result.Count.ShouldBe(2);
        result.ShouldContain(e => e.Expression == "@CurrentIteration" && e.Type == "relative");
        result.ShouldContain(e => e.Expression == @"Project\Sprint 1" && e.Type == "absolute");
    }

    [Fact]
    public async Task SetSprintIterations_ReplacesExisting()
    {
        await _modeStore.SetSprintIterationsAsync(new List<SprintIterationEntry>
        {
            new("@CurrentIteration", "relative")
        });

        await _modeStore.SetSprintIterationsAsync(new List<SprintIterationEntry>
        {
            new(@"Project\Sprint 2", "absolute")
        });

        var result = await _modeStore.GetSprintIterationsAsync();
        result.Count.ShouldBe(1);
        result[0].Expression.ShouldBe(@"Project\Sprint 2");
    }

    [Fact]
    public async Task SetSprintIterations_EmptyList_ClearsAll()
    {
        await _modeStore.SetSprintIterationsAsync(new List<SprintIterationEntry>
        {
            new("@CurrentIteration", "relative")
        });

        await _modeStore.SetSprintIterationsAsync(new List<SprintIterationEntry>());
        var result = await _modeStore.GetSprintIterationsAsync();
        result.ShouldBeEmpty();
    }

    // --- Area Paths ---

    [Fact]
    public async Task GetAreaPathsAsync_Empty_ReturnsEmptyList()
    {
        var entries = await _modeStore.GetAreaPathsAsync();
        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetAndGetAreaPaths_RoundTrip()
    {
        var entries = new List<WorkspaceAreaPath>
        {
            new(@"Project\TeamA", "under"),
            new(@"Project\TeamB", "exact")
        };

        await _modeStore.SetAreaPathsAsync(entries);
        var result = await _modeStore.GetAreaPathsAsync();

        result.Count.ShouldBe(2);
        result.ShouldContain(e => e.Path == @"Project\TeamA" && e.Semantics == "under");
        result.ShouldContain(e => e.Path == @"Project\TeamB" && e.Semantics == "exact");
    }

    [Fact]
    public async Task SetAreaPaths_ReplacesExisting()
    {
        await _modeStore.SetAreaPathsAsync(new List<WorkspaceAreaPath>
        {
            new(@"Project\TeamA", "under")
        });

        await _modeStore.SetAreaPathsAsync(new List<WorkspaceAreaPath>
        {
            new(@"Project\TeamB", "exact")
        });

        var result = await _modeStore.GetAreaPathsAsync();
        result.Count.ShouldBe(1);
        result[0].Path.ShouldBe(@"Project\TeamB");
    }

    [Fact]
    public async Task SetAreaPaths_EmptyList_ClearsAll()
    {
        await _modeStore.SetAreaPathsAsync(new List<WorkspaceAreaPath>
        {
            new(@"Project\TeamA", "under")
        });

        await _modeStore.SetAreaPathsAsync(new List<WorkspaceAreaPath>());
        var result = await _modeStore.GetAreaPathsAsync();
        result.ShouldBeEmpty();
    }

    // --- Schema ---

    [Fact]
    public void SchemaRebuilt_NewTablesExist()
    {
        // The tables should exist because SqliteCacheStore creates them
        var conn = _store.GetConnection();

        string[] tables = ["tracked_items", "excluded_items", "sprint_iterations", "area_paths"];
        foreach (var table in tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
            cmd.Parameters.AddWithValue("@name", table);
            cmd.ExecuteScalar().ShouldNotBeNull($"Table '{table}' should exist");
        }
    }

    [Fact]
    public void SchemaVersion_Is10()
    {
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        var result = cmd.ExecuteScalar() as string;
        result.ShouldBe("10");
    }
}
