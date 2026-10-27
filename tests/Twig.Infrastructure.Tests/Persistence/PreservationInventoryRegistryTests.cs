using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class PreservationInventoryRegistryTests
{
    [Fact]
    public void Registry_CoversEveryCurrentWorkspaceTableAndColumn()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var connection = store.GetConnection();

        foreach (var schema in new[] { "main", "pending" })
        {
            var expectedTables = PreservationInventory.AllTables
                .Where(table => table.Schema == schema)
                .Select(table => table.Table)
                .ToHashSet(StringComparer.Ordinal);
            var actualTables = ReadTables(connection, schema);
            actualTables.Order(StringComparer.Ordinal).ShouldBe(expectedTables.Order(StringComparer.Ordinal));
        }

        foreach (var table in PreservationInventory.AllTables)
        {
            ReadColumns(connection, table.Schema, table.Table).Order(StringComparer.Ordinal)
                .ShouldBe(table.Columns.Order(StringComparer.Ordinal));
        }

        PreservationInventory.AllRowSets
            .Select(rowSet => rowSet.ClassName)
            .Distinct(StringComparer.Ordinal)
            .Count()
            .ShouldBe(PreservationInventory.AllRowSets.Count);
    }

    [Theory]
    [InlineData("active_work_item_id")]
    [InlineData("workspace_mode")]
    [InlineData("nav_history_cursor")]
    [InlineData("area_tree_json")]
    [InlineData("area_tree_fetched_at")]
    [InlineData("last_refreshed_at")]
    public void Registry_RecognizesKnownContextKeys(string key)
        => PreservationInventory.IsKnownContextKey(key).ShouldBeTrue();

    [Theory]
    [InlineData("schema_version")]
    [InlineData("binding_generation")]
    [InlineData("process_configuration_data")]
    [InlineData("draft-served-evidence:abc")]
    public void Registry_RecognizesKnownMetadataKeys(string key)
        => PreservationInventory.IsKnownMetadataKey(key).ShouldBeTrue();

    private static HashSet<string> ReadTables(SqliteConnection connection, string schema)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM \"{schema}\".sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA \"{schema}\".table_info(\"{table}\");";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
