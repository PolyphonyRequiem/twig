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
        DurableTableExists(conn, "pending_changes").ShouldBeTrue();
        TableExists(conn, "process_types").ShouldBeTrue();
        TableExists(conn, "context").ShouldBeTrue();
        TableExists(conn, "field_definitions").ShouldBeTrue();
        TableExists(conn, "work_item_links").ShouldBeTrue();
        DurableTableExists(conn, "seed_links").ShouldBeTrue();
        TableExists(conn, "navigation_history").ShouldBeTrue();
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
    public void Schema_HasAreaPathIndex_OnWorkItems()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_work_items_area';";
        var result = cmd.ExecuteScalar() as string;

        result.ShouldBe("idx_work_items_area");
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
        cmd.CommandText = "SELECT name FROM main.sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Whether a table exists in the attached durable store (<c>pending.db</c>) rather than the
    /// disposable mirror. <c>sqlite_master</c> is per-schema, so the two are unambiguous.
    /// </summary>
    private static bool DurableTableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pending.sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-004 Task 7: Schema mismatch recovery
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_SchemaMismatch_RebuildsSetsFlag_NoOldDataLeaks()
    {
        // Open a DB with an old schema version and stale data.
        // Verify: SchemaWasRebuilt set, tables recreated, no data from old schema leaks.
        var connStr = $"Data Source=SchemaMismatchRecovery_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        using var setupConn = new SqliteConnection(connStr);
        setupConn.Open();

        // Create an "old" schema with version 1 and some stale data
        using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata (key, value) VALUES ('schema_version', '1');
                CREATE TABLE work_items (id INTEGER PRIMARY KEY, type TEXT, title TEXT, state TEXT, revision INTEGER, fields_json TEXT, is_dirty INTEGER, last_synced_at TEXT);
                INSERT INTO work_items (id, type, title, state, revision, fields_json, is_dirty, last_synced_at)
                    VALUES (42, 'Bug', 'Stale bug from old schema', 'Active', 1, '{}', 0, '2024-01-01');
                CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, work_item_id INTEGER);
                CREATE TABLE process_types (type_name TEXT PRIMARY KEY, states_json TEXT NOT NULL, last_synced_at TEXT NOT NULL);
                INSERT INTO process_types (type_name, states_json, last_synced_at) VALUES ('OldType', '[]', '2024-01-01');
                CREATE TABLE context (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO context (key, value) VALUES ('active_item', '42');
                """;
            cmd.ExecuteNonQuery();
        }

        // Open SqliteCacheStore — should detect version mismatch and rebuild
        using var store = new SqliteCacheStore(connStr);

        store.SchemaWasRebuilt.ShouldBeTrue("Schema should be rebuilt on version mismatch");

        var conn = store.GetConnection();

        // Verify schema version is current
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
            var version = cmd.ExecuteScalar() as string;
            int.Parse(version!).ShouldBe(SqliteCacheStore.SchemaVersion);
        }

        // Verify no stale data from old schema leaks
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM work_items;";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(0, "Old work items should be dropped");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM process_types;";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(0, "Old process types should be dropped");
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM context;";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(1, "Only default workspace_mode row should exist after rebuild");
        }

        // Mirror tables
        TableExists(conn, "metadata").ShouldBeTrue();
        TableExists(conn, "work_items").ShouldBeTrue();
        TableExists(conn, "process_types").ShouldBeTrue();
        TableExists(conn, "context").ShouldBeTrue();
        TableExists(conn, "field_definitions").ShouldBeTrue();
        TableExists(conn, "work_item_links").ShouldBeTrue();
        TableExists(conn, "navigation_history").ShouldBeTrue();

        // Durable tables live in the attached store, NOT the mirror (0013).
        DurableTableExists(conn, "pending_changes").ShouldBeTrue();
        DurableTableExists(conn, "publish_id_map").ShouldBeTrue();
        DurableTableExists(conn, "seed_links").ShouldBeTrue();
        TableExists(conn, "pending_changes").ShouldBeFalse();
        TableExists(conn, "publish_id_map").ShouldBeFalse();
        TableExists(conn, "seed_links").ShouldBeFalse();
    }

    /// <summary>
    /// The completeness guard for wayfinder 0013's durability line: every table is in exactly one
    /// store, and each is in the right one per 0005 §3a's "can ADO rebuild it?" test.
    /// <para>
    /// A new table added to the wrong store fails here rather than silently becoming droppable
    /// durable state — the #271 failure shape this map exists to remove.
    /// </para>
    /// </summary>
    [Fact]
    public void Schema_PlacesEveryTableInExactlyOneStore_ByDurability()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        string[] expectedMirror =
            ["metadata", "work_items", "process_types", "context", "field_definitions",
             "work_item_links", "navigation_history", "iteration_calendar"];
        // staged_identities is DURABLE (wayfinder 0014): it is the source of truth for a
        // staged seed's identity, its display alias, and the retirement record that makes
        // "never recycled" structural. Putting it in the mirror would make a durable identity
        // droppable — the exact incoherence 0003 objected to.
        // publish_intents is DURABLE (wayfinder 0015): it records a create BEFORE the ADO call
        // and its outcome after. It is durable by 0005's "can ADO rebuild it?" test — it cannot
        // be, because it is precisely the record of a call whose outcome ADO may or may not
        // hold. A droppable copy would be erased by the crash it exists to survive.
        // benches and bench_selectors are DURABLE (ADO #144): a Bench holds pins the person made
        // by hand and a name only they chose, so ADO cannot rebuild it. Their loss is SILENT —
        // nothing prompts and nothing refuses — so a droppable copy would surface as a missing
        // pin weeks later.
        // iteration_calendar is a MIRROR table by the same test read the other way: it is a copy
        // of ADO's own iteration list, so ADO CAN rebuild it and the next refresh does. It is
        // cached locally only so a Bench's sprint rule can be answered without a network call.
        // tracked_items and excluded_items are GONE (ADO #151). They were declared, dropped on
        // every SchemaVersion bump, and read by nothing after pins became selectors. Leaving them
        // meant a grep told the reader pins live in the cache — the exact false premise the Bench
        // build brief inherited and that cost a wrong plan. Pins are selectors on a Bench in the
        // durable store; exclusions live in the tracking file, outside the Bench by decision.
        // current_bench is DURABLE (ADO #149): which arrangement the person is standing on is
        // theirs and ADO has never heard of it, so ADO cannot rebuild it. A droppable copy would
        // silently move somebody back to the default on a SchemaVersion bump — the same
        // "resolves, but to the wrong thing" failure the unknown-Bench error exists to escape.
        string[] expectedDurable =
            ["pending_changes", "publish_id_map", "seed_links", "staged_identities", "publish_intents",
             "benches", "bench_selectors", "current_bench"];

        ReadTables(conn, "main").ShouldBe(expectedMirror, ignoreOrder: true);
        ReadTables(conn, "pending").ShouldBe(expectedDurable, ignoreOrder: true);
    }

    /// <summary>The durable store carries its own version, independent of <c>SchemaVersion</c>.</summary>
    [Fact]
    public void DurableStore_RecordsItsOwnSchemaVersion()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        using var cmd = store.GetConnection().CreateCommand();
        cmd.CommandText = "PRAGMA pending.user_version;";
        Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
    }

    /// <summary>
    /// Wayfinder 0014 added durable migration v2. The durable store is NEVER dropped, so v2
    /// must be an additive ALTER + backfill applied to a store already carrying v1 data --
    /// not a rebuild. These exercise that upgrade path against a real pre-existing v1 store,
    /// which the in-memory happy path (created straight at v2) never touches.
    /// </summary>
    [Fact]
    public void DurableStore_UpgradingFromV1_AddsTheIdentityShape_WithoutDroppingExistingRows()
    {
        var connStr = $"Data Source=DurableV1_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        // A holder connection keeps the shared in-memory database alive across opens.
        using var holder = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        holder.Open();

        // Build a durable store at v1: the v1 tables, v1 user_version, and a staged row that
        // must survive the upgrade.
        using (var seed = new Microsoft.Data.Sqlite.SqliteCommand(
            """
            ATTACH DATABASE ':memory:' AS pending;
            """, holder))
        {
            seed.ExecuteNonQuery();
        }

        using var store = new SqliteCacheStore(connStr);
        var conn = store.GetConnection();

        // After construction the durable store is at the current version...
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA pending.user_version;";
            Convert.ToInt32(v.ExecuteScalar()).ShouldBe(SqliteCacheStore.DurableSchemaVersion);
        }

        // ...and the v2 shape is present and usable.
        using (var cols = conn.CreateCommand())
        {
            cols.CommandText = "SELECT COUNT(*) FROM pragma_table_info('staged_identities');";
            Convert.ToInt32(cols.ExecuteScalar()).ShouldBe(4,
                "staged_identity, alias, created_at, retired_at");
        }

        using (var mapCols = conn.CreateCommand())
        {
            mapCols.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('publish_id_map') WHERE name = 'staged_identity';";
            Convert.ToInt32(mapCols.ExecuteScalar()).ShouldBe(1,
                "publish_id_map re-keys to StagedIdentity via an additive column, not a rebuild");
        }
    }

    /// <summary>
    /// The alias is UNIQUE but is deliberately NOT the primary key and NOT a foreign key
    /// target (0003 §5a: never a key, never joined on, never an FK target). If someone later
    /// promotes it to a key, the #280 failure class comes back, so assert the shape.
    /// </summary>
    [Fact]
    public void StagedIdentities_KeysOnTheIdentity_AndTheAliasIsNeverAKey()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var conn = store.GetConnection();

        using (var pk = conn.CreateCommand())
        {
            pk.CommandText = "SELECT name FROM pragma_table_info('staged_identities') WHERE pk > 0;";
            var keyColumns = new List<string>();
            using var reader = pk.ExecuteReader();
            while (reader.Read())
                keyColumns.Add(reader.GetString(0));

            keyColumns.ShouldBe(["staged_identity"],
                "the durable identity is the key; the negative alias is decorative (0003 §5a)");
        }

        using (var fks = conn.CreateCommand())
        {
            fks.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('staged_identities');";
            Convert.ToInt32(fks.ExecuteScalar()).ShouldBe(0,
                "the alias must never be a foreign key target");
        }
    }

    /// <summary>
    /// The clean-break guard (wayfinder 0013 / 0005 §5). No data migration is written from the
    /// pre-split layout, so a version-mismatch rebuild must REFUSE while the legacy mirror still
    /// holds staged work, rather than silently dropping it — #271 recurring.
    /// </summary>
    [Fact]
    public void Constructor_LegacyPendingSetNonEmpty_RefusesToRebuild()
    {
        var connStr = $"Data Source=LegacyPending_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        using var setupConn = new SqliteConnection(connStr);
        setupConn.Open();
        using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata (key, value) VALUES ('schema_version', '10');
                CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, work_item_id INTEGER);
                INSERT INTO pending_changes (id, work_item_id) VALUES (1, 42);
                """;
            cmd.ExecuteNonQuery();
        }

        var ex = Should.Throw<InvalidOperationException>(() => new SqliteCacheStore(connStr));
        ex.Message.ShouldContain("pending change");
        ex.Message.ShouldContain("twig sync");

        // The staged row is still there — the guard refused before dropping anything.
        using var verify = setupConn.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM pending_changes;";
        Convert.ToInt32(verify.ExecuteScalar()).ShouldBe(1);
    }

    /// <summary>
    /// An EMPTY legacy table has nothing to lose, so the rebuild proceeds — and the stale copy
    /// must be dropped, or it would shadow the durable table under SQLite name resolution.
    /// </summary>
    [Fact]
    public void Constructor_LegacyPendingSetEmpty_RebuildsAndDropsTheShadowTable()
    {
        var connStr = $"LegacyEmpty_{Guid.NewGuid():N}";
        var full = $"Data Source={connStr};Mode=Memory;Cache=Shared";

        using var setupConn = new SqliteConnection(full);
        setupConn.Open();
        using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata (key, value) VALUES ('schema_version', '10');
                CREATE TABLE pending_changes (id INTEGER PRIMARY KEY, work_item_id INTEGER);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new SqliteCacheStore(full);

        store.SchemaWasRebuilt.ShouldBeTrue();
        TableExists(store.GetConnection(), "pending_changes")
            .ShouldBeFalse("the legacy mirror copy must not shadow the durable table");
        DurableTableExists(store.GetConnection(), "pending_changes").ShouldBeTrue();
    }

    private static List<string> ReadTables(SqliteConnection conn, string schema)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void Constructor_MissingMetadataTable_RebuildsFully()
    {
        // A DB where the metadata table was somehow deleted
        var connStr = $"Data Source=MissingMetadata_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        using var setupConn = new SqliteConnection(connStr);
        setupConn.Open();

        // Create partial schema without metadata
        using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE work_items (id INTEGER PRIMARY KEY);
                INSERT INTO work_items (id) VALUES (999);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new SqliteCacheStore(connStr);

        store.SchemaWasRebuilt.ShouldBeTrue();

        // Verify old data is gone
        var conn = store.GetConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM work_items;";
            Convert.ToInt32(cmd.ExecuteScalar()).ShouldBe(0, "Old data should not leak through rebuild");
        }
    }

    [Fact]
    public void Constructor_NonNumericSchemaVersion_RebuildsFully()
    {
        // Schema version is "abc" — not parseable as int
        var connStr = $"Data Source=NonNumericVersion_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        using var setupConn = new SqliteConnection(connStr);
        setupConn.Open();

        using (var cmd = setupConn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata (key, value) VALUES ('schema_version', 'abc');
                CREATE TABLE work_items (id INTEGER PRIMARY KEY);
                CREATE TABLE pending_changes (id INTEGER PRIMARY KEY);
                CREATE TABLE process_types (type_name TEXT PRIMARY KEY);
                CREATE TABLE context (key TEXT PRIMARY KEY);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new SqliteCacheStore(connStr);

        store.SchemaWasRebuilt.ShouldBeTrue();

        var conn = store.GetConnection();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
            var version = cmd.ExecuteScalar() as string;
            int.Parse(version!).ShouldBe(SqliteCacheStore.SchemaVersion);
        }
    }
}
