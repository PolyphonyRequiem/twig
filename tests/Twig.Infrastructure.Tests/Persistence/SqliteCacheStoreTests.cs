using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Tests for SqliteCacheStore: schema creation, version tracking, mismatch rebuild, WAL mode.
/// Uses :memory: databases for isolation.
/// </summary>
public class SqliteCacheStoreTests
{
    [Fact]
    public void Constructor_CreatesSchema_InMemory()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        // Verify all tables exist
        TableExists(conn, "metadata").ShouldBeTrue();
        TableExists(conn, "work_items").ShouldBeTrue();
        TableExists(conn, "pending_changes").ShouldBeTrue();
        TableExists(conn, "process_types").ShouldBeTrue();
        TableExists(conn, "context").ShouldBeTrue();
    }

    [Fact]
    public void Constructor_WritesSchemaVersion()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        var version = cmd.ExecuteScalar() as string;

        version.ShouldNotBeNull();
        int.Parse(version).ShouldBe(SqliteCacheStore.SchemaVersion);
    }

    [Fact]
    public void Constructor_DoesNotThrow_WhenEnablingWalMode()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        var mode = cmd.ExecuteScalar() as string;

        // In-memory databases report "memory"; file-based would report "wal".
        // The assertion verifies the PRAGMA executed without error.
        mode.ShouldNotBeNull();
        mode.ShouldBeOneOf("memory", "wal");
    }

    [Fact]
    public void Constructor_SchemaRebuilt_WhenNewDatabase()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        store.SchemaWasRebuilt.ShouldBeTrue();
    }

    [Fact]
    public void Constructor_RebuildSchema_OnVersionMismatch()
    {
        // Create a shared in-memory database with a name
        var connStr = "Data Source=VersionMismatchTest;Mode=Memory;Cache=Shared";

        // First, create a database with a wrong schema version
        using (var setupConn = new SqliteConnection(connStr))
        {
            setupConn.Open();
            using var cmd = setupConn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata (key, value) VALUES ('schema_version', '999');
                CREATE TABLE work_items (id INTEGER PRIMARY KEY);
                CREATE TABLE pending_changes (id INTEGER PRIMARY KEY);
                CREATE TABLE process_types (type_name TEXT PRIMARY KEY);
                CREATE TABLE context (key TEXT PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();

            // Open the store — it should detect version mismatch and rebuild
            using var store = new SqliteCacheStore(connStr);
            store.SchemaWasRebuilt.ShouldBeTrue();

            // Verify the schema version was updated
            var conn = store.GetConnection();
            using var verifyCmd = conn.CreateCommand();
            verifyCmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
            var version = verifyCmd.ExecuteScalar() as string;
            version.ShouldNotBeNull();
            int.Parse(version).ShouldBe(SqliteCacheStore.SchemaVersion);
        }
    }

    [Fact]
    public void GetConnection_ReturnsSameConnection()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn1 = store.GetConnection();
        var conn2 = store.GetConnection();
        conn1.ShouldBeSameAs(conn2);
    }

    [Fact]
    public void SchemaVersion_IsFour()
    {
        SqliteCacheStore.SchemaVersion.ShouldBe(4);
    }

    [Fact]
    public void ProcessTypes_HasColorHexAndIconIdColumns()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(process_types);";
        using var reader = cmd.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1)); // column name is at index 1
        }

        columns.ShouldContain("color_hex");
        columns.ShouldContain("icon_id");
    }

    [Fact]
    public void Constructor_SetsBusyTimeout()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout;";
        var timeout = Convert.ToInt32(cmd.ExecuteScalar());

        timeout.ShouldBe(5000);
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() is not null;
    }
}
