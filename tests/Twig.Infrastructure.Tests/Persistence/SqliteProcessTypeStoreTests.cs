using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for <see cref="SqliteProcessTypeStore"/> against an in-memory SQLite database.
/// </summary>
public class SqliteProcessTypeStoreTests : IDisposable
{
    private readonly SqliteCacheStore _store;
    private readonly SqliteProcessTypeStore _processTypeStore;

    public SqliteProcessTypeStoreTests()
    {
        _store = new SqliteCacheStore("Data Source=:memory:");
        _processTypeStore = new SqliteProcessTypeStore(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task GetByNameAsync_NotFound_ReturnsNull()
    {
        var result = await _processTypeStore.GetByNameAsync("NonExistent");
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAndGetByName_RoundTrip()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "Scenario",
            States = new StateEntry[] { new("Draft", StateCategory.Unknown, null), new("Active", StateCategory.Unknown, null), new("Done", StateCategory.Unknown, null) },
            DefaultChildType = "Task",
            ValidChildTypes = new[] { "Task" },
            ColorHex = "009CCC",
            IconId = "icon_list",
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("Scenario");

        loaded.ShouldNotBeNull();
        loaded.TypeName.ShouldBe("Scenario");
        loaded.States.Select(s => s.Name).ShouldBe(new[] { "Draft", "Active", "Done" });
        loaded.DefaultChildType.ShouldBe("Task");
        loaded.ValidChildTypes.ShouldBe(new[] { "Task" });
        loaded.ColorHex.ShouldBe("009CCC");
        loaded.IconId.ShouldBe("icon_list");
    }

    [Fact]
    public async Task SaveAsync_Upsert_UpdatesExistingRecord()
    {
        var original = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States = new StateEntry[] { new("New", StateCategory.Unknown, null), new("Active", StateCategory.Unknown, null) },
            ValidChildTypes = Array.Empty<string>(),
        };
        await _processTypeStore.SaveAsync(original);

        var updated = new ProcessTypeRecord
        {
            TypeName = "Bug",
            States = new StateEntry[] { new("New", StateCategory.Unknown, null), new("Active", StateCategory.Unknown, null), new("Fixed", StateCategory.Unknown, null) },
            DefaultChildType = "Task",
            ValidChildTypes = new[] { "Task" },
        };
        await _processTypeStore.SaveAsync(updated);

        var loaded = await _processTypeStore.GetByNameAsync("Bug");
        loaded.ShouldNotBeNull();
        loaded.States.Select(s => s.Name).ShouldBe(new[] { "New", "Active", "Fixed" });
        loaded.DefaultChildType.ShouldBe("Task");
        loaded.ValidChildTypes.ShouldBe(new[] { "Task" });
    }

    [Fact]
    public async Task GetAllAsync_Empty_ReturnsEmptyList()
    {
        var result = await _processTypeStore.GetAllAsync();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_MultipleRecords_ReturnsAll()
    {
        await _processTypeStore.SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Scenario",
            States = new StateEntry[] { new("New", StateCategory.Unknown, null), new("Done", StateCategory.Unknown, null) },
            ValidChildTypes = Array.Empty<string>(),
        });
        await _processTypeStore.SaveAsync(new ProcessTypeRecord
        {
            TypeName = "Deliverable",
            States = new StateEntry[] { new("Draft", StateCategory.Unknown, null), new("Active", StateCategory.Unknown, null) },
            ValidChildTypes = Array.Empty<string>(),
        });

        var result = await _processTypeStore.GetAllAsync();

        result.Count.ShouldBe(2);
        result.ShouldContain(r => r.TypeName == "Scenario");
        result.ShouldContain(r => r.TypeName == "Deliverable");
    }

    [Fact]
    public async Task SaveAsync_NullOptionalFields_StoredAndRetrievedCorrectly()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "MinimalType",
            States = new StateEntry[] { new("Active", StateCategory.Unknown, null) },
            ValidChildTypes = Array.Empty<string>(),
            DefaultChildType = null,
            ColorHex = null,
            IconId = null,
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("MinimalType");

        loaded.ShouldNotBeNull();
        loaded.DefaultChildType.ShouldBeNull();
        loaded.ColorHex.ShouldBeNull();
        loaded.IconId.ShouldBeNull();
        loaded.ValidChildTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAsync_EmptyStates_StoresEmptyArray()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "NoStates",
            States = Array.Empty<StateEntry>(),
            ValidChildTypes = Array.Empty<string>(),
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("NoStates");

        loaded.ShouldNotBeNull();
        loaded.States.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAsync_MultipleValidChildTypes_PreservesOrder()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "Initiative",
            States = new StateEntry[] { new("New", StateCategory.Unknown, null) },
            ValidChildTypes = new[] { "Feature", "Scenario", "Deliverable" },
            DefaultChildType = "Feature",
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("Initiative");

        loaded.ShouldNotBeNull();
        loaded.ValidChildTypes.ShouldBe(new[] { "Feature", "Scenario", "Deliverable" });
    }

    [Fact]
    public async Task GetByNameAsync_CorruptStatesJson_ReturnsEmptyStatesGracefully()
    {
        // Insert a row with corrupt states_json directly via SQL to simulate data corruption.
        // Verifies that DeserializeList catches JsonException and returns an empty array
        // instead of throwing, so the application degrades gracefully.
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_types (type_name, states_json, last_synced_at)
            VALUES ('CorruptType', 'NOT_VALID_JSON{{{', datetime('now'))
            """;
        cmd.ExecuteNonQuery();

        var loaded = await _processTypeStore.GetByNameAsync("CorruptType");

        loaded.ShouldNotBeNull();
        loaded.TypeName.ShouldBe("CorruptType");
        loaded.States.ShouldBeEmpty();
        loaded.ValidChildTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAllAsync_CorruptValidChildTypesJson_ReturnsEmptyChildTypesGracefully()
    {
        // Insert a row with corrupt valid_child_types_json to verify graceful degradation.
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_types (type_name, states_json, valid_child_types_json, last_synced_at)
            VALUES ('TypeWithCorruptChildren', '[{"name":"Active","category":"Unknown"}]', '{INVALID', datetime('now'))
            """;
        cmd.ExecuteNonQuery();

        var records = await _processTypeStore.GetAllAsync();

        records.ShouldNotBeEmpty();
        var record = records.First(r => r.TypeName == "TypeWithCorruptChildren");
        record.States.Select(s => s.Name).ShouldBe(new[] { "Active" });
        record.ValidChildTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAndGetByName_RoundTripsStateEntryWithCategoryAndColor()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "Feature",
            States = new StateEntry[]
            {
                new("New", StateCategory.Proposed, "b2b2b2"),
                new("Active", StateCategory.InProgress, "007acc"),
                new("Resolved", StateCategory.Resolved, "ff9d00"),
                new("Closed", StateCategory.Completed, "339933"),
                new("Removed", StateCategory.Removed, "ffffff"),
            },
            DefaultChildType = "User Story",
            ValidChildTypes = new[] { "User Story", "Bug" },
            ColorHex = "773B93",
            IconId = "icon_trophy",
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("Feature");

        loaded.ShouldNotBeNull();
        loaded.TypeName.ShouldBe("Feature");
        loaded.States.Count.ShouldBe(5);

        loaded.States[0].Name.ShouldBe("New");
        loaded.States[0].Category.ShouldBe(StateCategory.Proposed);
        loaded.States[0].Color.ShouldBe("b2b2b2");

        loaded.States[1].Name.ShouldBe("Active");
        loaded.States[1].Category.ShouldBe(StateCategory.InProgress);
        loaded.States[1].Color.ShouldBe("007acc");

        loaded.States[2].Name.ShouldBe("Resolved");
        loaded.States[2].Category.ShouldBe(StateCategory.Resolved);
        loaded.States[2].Color.ShouldBe("ff9d00");

        loaded.States[3].Name.ShouldBe("Closed");
        loaded.States[3].Category.ShouldBe(StateCategory.Completed);
        loaded.States[3].Color.ShouldBe("339933");

        loaded.States[4].Name.ShouldBe("Removed");
        loaded.States[4].Category.ShouldBe(StateCategory.Removed);
        loaded.States[4].Color.ShouldBe("ffffff");

        loaded.DefaultChildType.ShouldBe("User Story");
        loaded.ValidChildTypes.ShouldBe(new[] { "User Story", "Bug" });
        loaded.ColorHex.ShouldBe("773B93");
        loaded.IconId.ShouldBe("icon_trophy");
    }

    [Fact]
    public async Task SaveAndGetByName_NullColor_RoundTripsCorrectly()
    {
        var record = new ProcessTypeRecord
        {
            TypeName = "Task",
            States = new StateEntry[]
            {
                new("New", StateCategory.Proposed, null),
                new("Active", StateCategory.InProgress, "007acc"),
            },
            ValidChildTypes = Array.Empty<string>(),
        };

        await _processTypeStore.SaveAsync(record);
        var loaded = await _processTypeStore.GetByNameAsync("Task");

        loaded.ShouldNotBeNull();
        loaded.States[0].Name.ShouldBe("New");
        loaded.States[0].Category.ShouldBe(StateCategory.Proposed);
        loaded.States[0].Color.ShouldBeNull();

        loaded.States[1].Name.ShouldBe("Active");
        loaded.States[1].Category.ShouldBe(StateCategory.InProgress);
        loaded.States[1].Color.ShouldBe("007acc");
    }

    [Fact]
    public async Task GetProcessConfigurationDataAsync_NoData_ReturnsNull()
    {
        var result = await _processTypeStore.GetProcessConfigurationDataAsync();
        result.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAndGetProcessConfigurationData_RoundTrip()
    {
        var config = new ProcessConfigurationData
        {
            TaskBacklog = new BacklogLevelConfiguration
            {
                Name = "Tasks",
                WorkItemTypeNames = new[] { "Task" },
            },
            RequirementBacklog = new BacklogLevelConfiguration
            {
                Name = "Stories",
                WorkItemTypeNames = new[] { "User Story", "Bug" },
            },
            PortfolioBacklogs = new List<BacklogLevelConfiguration>
            {
                new() { Name = "Epics", WorkItemTypeNames = new[] { "Epic" } },
                new() { Name = "Features", WorkItemTypeNames = new[] { "Feature" } },
            },
            BugWorkItems = new BacklogLevelConfiguration
            {
                Name = "Bugs",
                WorkItemTypeNames = new[] { "Bug" },
            },
        };

        await _processTypeStore.SaveProcessConfigurationDataAsync(config);
        var loaded = await _processTypeStore.GetProcessConfigurationDataAsync();

        loaded.ShouldNotBeNull();

        loaded.TaskBacklog.ShouldNotBeNull();
        loaded.TaskBacklog!.Name.ShouldBe("Tasks");
        loaded.TaskBacklog.WorkItemTypeNames.ShouldBe(new[] { "Task" });

        loaded.RequirementBacklog.ShouldNotBeNull();
        loaded.RequirementBacklog!.Name.ShouldBe("Stories");
        loaded.RequirementBacklog.WorkItemTypeNames.ShouldBe(new[] { "User Story", "Bug" });

        loaded.PortfolioBacklogs.Count.ShouldBe(2);
        loaded.PortfolioBacklogs[0].Name.ShouldBe("Epics");
        loaded.PortfolioBacklogs[0].WorkItemTypeNames.ShouldBe(new[] { "Epic" });
        loaded.PortfolioBacklogs[1].Name.ShouldBe("Features");
        loaded.PortfolioBacklogs[1].WorkItemTypeNames.ShouldBe(new[] { "Feature" });

        loaded.BugWorkItems.ShouldNotBeNull();
        loaded.BugWorkItems!.Name.ShouldBe("Bugs");
        loaded.BugWorkItems.WorkItemTypeNames.ShouldBe(new[] { "Bug" });
    }

    [Fact]
    public async Task SaveProcessConfigurationDataAsync_OverwritesPreviousData()
    {
        var original = new ProcessConfigurationData
        {
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Original", WorkItemTypeNames = new[] { "Story" } },
        };
        await _processTypeStore.SaveProcessConfigurationDataAsync(original);

        var updated = new ProcessConfigurationData
        {
            RequirementBacklog = new BacklogLevelConfiguration { Name = "Updated", WorkItemTypeNames = new[] { "User Story", "Bug" } },
        };
        await _processTypeStore.SaveProcessConfigurationDataAsync(updated);

        var loaded = await _processTypeStore.GetProcessConfigurationDataAsync();
        loaded.ShouldNotBeNull();
        loaded!.RequirementBacklog!.Name.ShouldBe("Updated");
        loaded.RequirementBacklog.WorkItemTypeNames.ShouldBe(new[] { "User Story", "Bug" });
    }

    [Fact]
    public async Task GetProcessConfigurationDataAsync_CorruptJson_ReturnsNull()
    {
        // Insert corrupt JSON directly to simulate data corruption
        var conn = _store.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO metadata (key, value) VALUES ('process_configuration_data', 'NOT_VALID_JSON{{{')";
        cmd.ExecuteNonQuery();

        var result = await _processTypeStore.GetProcessConfigurationDataAsync();
        result.ShouldBeNull();
    }
}
