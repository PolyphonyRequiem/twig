using Microsoft.Data.Sqlite;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Manages the SQLite database lifecycle: creation, schema versioning, WAL mode, and connection access.
/// Single connection per CLI invocation — no thread-safety needed.
/// </summary>
public sealed class SqliteCacheStore : IDisposable
{
    /// <summary>
    /// Current schema version compiled into the binary.
    /// If the DB schema version differs, all tables are dropped and recreated.
    /// </summary>
    internal const int SchemaVersion = 12;

    /// <summary>
    /// Schema version of the durable store (<c>pending.db</c>), versioned independently of
    /// <see cref="SchemaVersion"/>.
    /// <para>
    /// Unlike the mirror, the durable store is <b>never dropped and recreated</b> — it holds the
    /// only copy of work that ADO has never seen. Every future shape change here must be an
    /// additive migration in <see cref="DurableMigrations"/>, and this number bumped to match.
    /// </para>
    /// </summary>
    internal const int DurableSchemaVersion = 3;

    /// <summary>The schema name the durable store is ATTACHed under.</summary>
    internal const string DurableSchema = "pending";

    private readonly SqliteConnection _connection;
    private bool _schemaRebuilt;

    static SqliteCacheStore()
    {
        SQLitePCL.Batteries.Init();
    }

    /// <summary>
    /// Derives the durable store's path from the mirror's. A file-backed mirror gets a sibling
    /// <c>pending.db</c>; an in-memory mirror gets a private in-memory durable store, so tests
    /// and benchmarks need no second path.
    /// </summary>
    internal static string DeriveDurableDataSource(string mirrorDataSource)
    {
        if (string.IsNullOrWhiteSpace(mirrorDataSource))
            return ":memory:";

        // ":memory:" and the "file::memory:" / "mode=memory" family have no directory to be a
        // sibling of. A bare ":memory:" ATTACH is a distinct private database, which is the
        // correct disposable-mirror/durable-store pairing for an in-memory cache.
        if (mirrorDataSource.Contains(":memory:", StringComparison.OrdinalIgnoreCase)
            || mirrorDataSource.Contains("mode=memory", StringComparison.OrdinalIgnoreCase))
            return ":memory:";

        var dir = Path.GetDirectoryName(mirrorDataSource);
        return string.IsNullOrEmpty(dir) ? "pending.db" : Path.Combine(dir, "pending.db");
    }

    /// <summary>
    /// Opens (or creates) the SQLite database at the given connection string.
    /// Enables WAL mode, attaches the durable store, checks schema version, and creates/rebuilds
    /// tables as needed. Wraps open in try-catch for corruption detection (FM-008).
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g., "Data Source=.twig/twig.db" or "Data Source=:memory:").</param>
    public SqliteCacheStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            EnableWalMode();
            AttachDurableStore();
            EnsureSchema();
            EnsureDurableSchema();
        }
        catch (SqliteException ex)
        {
            _connection.Dispose();
            // I-003: Preserve the original exception chain for debugging.
            // #271: open-time failures include locked, read-only and permission cases that are
            // NOT corruption, so the message is derived from the error code instead of asserting
            // corruption unconditionally. ExceptionHandler unwraps this and branches on
            // SqliteErrorCode again to choose the user-facing advice.
            var primary = ex.SqliteErrorCode & 0xFF;
            var message = primary is 11 or 26   // SQLITE_CORRUPT / SQLITE_NOTADB
                ? $"The twig cache is corrupt and cannot be opened: {ex.Message}"
                : $"Failed to open the twig cache: {ex.Message}";
            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>
    /// Gets the open SQLite connection.
    /// </summary>
    public SqliteConnection GetConnection() => _connection;

    /// <summary>
    /// The currently active ambient transaction, if any.
    /// Set by <see cref="SqliteUnitOfWork.BeginAsync"/> and cleared on commit, rollback, or dispose.
    /// Repository implementations use this to enroll commands in the active transaction.
    /// </summary>
    internal SqliteTransaction? ActiveTransaction { get; set; }

    /// <summary>
    /// Indicates whether the schema was rebuilt during initialization (version mismatch or missing).
    /// </summary>
    public bool SchemaWasRebuilt => _schemaRebuilt;

    private void EnableWalMode()
    {
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        using var busyCmd = _connection.CreateCommand();
        busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
        busyCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Attaches the durable store as schema <c>pending</c>, next to the pragmas.
    /// <para>
    /// 0005 §4 measured that one <c>BeginTransaction</c> spans both files and that rollback
    /// undoes both under WAL, so <see cref="SqliteUnitOfWork"/> and the publish transaction keep
    /// their semantics. Because SQLite resolves an unqualified table name across every attached
    /// schema, repository SQL referring to durable tables needs no schema prefix.
    /// </para>
    /// </summary>
    private void AttachDurableStore()
    {
        var dataSource = new SqliteConnectionStringBuilder(_connection.ConnectionString).DataSource;
        var durableSource = DeriveDurableDataSource(dataSource);

        // Microsoft.Data.Sqlite pools connections by connection string, and a pooled connection
        // keeps its ATTACHes. Re-attaching would fail with "database pending is already in use",
        // and a stale attach could point at a since-deleted file, so detach first.
        DetachDurableStoreIfAttached();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE @path AS {DurableSchema};";
        cmd.Parameters.AddWithValue("@path", durableSource);
        cmd.ExecuteNonQuery();

        // WAL is per-database: the attached file needs its own journal_mode.
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = $"PRAGMA {DurableSchema}.journal_mode=WAL;";
        walCmd.ExecuteNonQuery();
    }

    private void DetachDurableStoreIfAttached()
    {
        using (var listCmd = _connection.CreateCommand())
        {
            listCmd.CommandText = "SELECT name FROM pragma_database_list WHERE name = @name;";
            listCmd.Parameters.AddWithValue("@name", DurableSchema);
            if (listCmd.ExecuteScalar() is null)
                return;
        }

        using var detachCmd = _connection.CreateCommand();
        detachCmd.CommandText = $"DETACH DATABASE {DurableSchema};";
        detachCmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        if (!SchemaExists() || !SchemaVersionMatches())
        {
            GuardLegacyPendingSet();
            DropAllTables();
            DropLegacyDurableTables();
            CreateSchema();
            WriteSchemaVersion();
            _schemaRebuilt = true;
        }
    }

    /// <summary>
    /// The clean-break guard (0005 §5, wayfinder 0013 — <b>not optional</b>).
    /// <para>
    /// No data migration is written from the pre-split layout, so a rebuild would silently
    /// destroy staged notes and field edits that live only in the old <c>twig.db</c>. That is
    /// #271 recurring: a healthy-cache rebuild that eats unpushed work. So when the legacy
    /// mirror still holds a non-empty pending set, refuse to rebuild and tell the user to push
    /// or discard first.
    /// </para>
    /// </summary>
    private void GuardLegacyPendingSet()
    {
        if (!LegacyMirrorTableExists("pending_changes"))
            return;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM main.pending_changes;";
        var pending = Convert.ToInt32(cmd.ExecuteScalar());
        if (pending == 0)
            return;

        throw new InvalidOperationException(
            $"This twig cache holds {pending} pending change(s) staged under the previous storage " +
            "layout, and upgrading would discard them.\n\n" +
            "Push or discard them with the previous twig version first:\n" +
            "  twig sync      # push staged changes to Azure DevOps\n" +
            "  twig discard   # abandon them\n\n" +
            "Then re-run this command.");
    }

    /// <summary>
    /// Removes empty pre-split copies of the durable tables from the mirror. SQLite resolves an
    /// unqualified name against <c>main</c> first, so a leftover legacy table would shadow the
    /// durable one and silently take the writes.
    /// </summary>
    private void DropLegacyDurableTables()
    {
        foreach (var table in new[] { "pending_changes", "publish_id_map", "seed_links" })
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS main.{table};";
            cmd.ExecuteNonQuery();
        }
    }

    private bool LegacyMirrorTableExists(string table)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM main.sqlite_master WHERE type='table' AND name=@name;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Brings the durable store up to <see cref="DurableSchemaVersion"/> by applying each
    /// outstanding migration in order inside one transaction.
    /// <para>
    /// <b>This store is never dropped.</b> Migrations are additive (CREATE / ALTER / backfill)
    /// and must stay idempotent-safe in ordering: the applied version is the only state that
    /// decides what runs.
    /// </para>
    /// </summary>
    private void EnsureDurableSchema()
    {
        var from = ReadDurableSchemaVersion();
        if (from >= DurableSchemaVersion)
            return;

        using var tx = _connection.BeginTransaction();
        try
        {
            for (var v = from + 1; v <= DurableSchemaVersion; v++)
            {
                if (!DurableMigrations.TryGetValue(v, out var sql))
                    throw new InvalidOperationException(
                        $"No migration registered for durable schema version {v}. " +
                        "DurableSchemaVersion was bumped without adding its migration.");

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            using (var versionCmd = _connection.CreateCommand())
            {
                versionCmd.Transaction = tx;
                versionCmd.CommandText = $"PRAGMA {DurableSchema}.user_version = {DurableSchemaVersion};";
                versionCmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private int ReadDurableSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"PRAGMA {DurableSchema}.user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// The durable store's migration ledger, keyed by the version each one produces.
    /// Version 1 is the initial shape; later entries must be ALTER + backfill, never a rebuild.
    /// </summary>
    private static readonly Dictionary<int, string> DurableMigrations = new()
    {
        [1] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.pending_changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                work_item_id INTEGER NOT NULL,
                change_type TEXT NOT NULL,
                field_name TEXT,
                old_value TEXT,
                new_value TEXT,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_pending_changes_item ON pending_changes(work_item_id);

            CREATE TABLE IF NOT EXISTS {DurableSchema}.publish_id_map (
                old_id INTEGER PRIMARY KEY,
                new_id INTEGER NOT NULL,
                published_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS {DurableSchema}.seed_links (
                source_id INTEGER NOT NULL,
                target_id INTEGER NOT NULL,
                link_type TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (source_id, target_id, link_type)
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_seed_links_source ON seed_links(source_id);
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_seed_links_target ON seed_links(target_id);
            """,

        // Wayfinder 0014. The durable half of the seed identity model.
        //
        // WHY A SEPARATE TABLE, when work_items already has an is_seed flag: work_items is in
        // the DISPOSABLE mirror. A durable identity on a droppable row is the exact incoherence
        // 0003 objected to, so the identity, the alias and the retirement record live HERE, in
        // the store a SchemaVersion bump cannot reach. The mirror keeps a staged_identity
        // column purely as a join-free convenience for reads; this table is the source of truth
        // and can rebuild it.
        //
        // The `alias` column is UNIQUE but is deliberately NOT the primary key and is NOT a
        // foreign key target anywhere (0003 §5a). `retired_at` is what makes "never recycled"
        // structural: a discarded seed's row is marked, never deleted, so MIN(alias) can never
        // walk back over an issued number.
        [2] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.staged_identities (
                staged_identity TEXT PRIMARY KEY,
                alias INTEGER NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                retired_at TEXT
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_staged_identities_alias ON staged_identities(alias);

            ALTER TABLE {DurableSchema}.publish_id_map ADD COLUMN staged_identity TEXT;
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_publish_id_map_staged_identity
                ON publish_id_map(staged_identity);

            INSERT OR IGNORE INTO {DurableSchema}.staged_identities (staged_identity, alias, created_at, retired_at)
            SELECT lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-7' || substr(hex(randomblob(2)), 2)
                       || '-' || substr('89ab', abs(random()) % 4 + 1, 1) || substr(hex(randomblob(2)), 2)
                       || '-' || hex(randomblob(6))),
                   old_id,
                   published_at,
                   published_at
            FROM {DurableSchema}.publish_id_map
            WHERE old_id < 0;

            UPDATE {DurableSchema}.publish_id_map
            SET staged_identity = (
                SELECT si.staged_identity FROM {DurableSchema}.staged_identities si
                WHERE si.alias = publish_id_map.old_id
            )
            WHERE staged_identity IS NULL AND old_id < 0;
            """,

        // Wayfinder 0015. The durable intent record — 0001 §4's "record intent before the call,
        // record the outcome after it".
        //
        // WHY IT IS DURABLE by 0005's test ("can ADO rebuild it?"): no. This is the record of a
        // call whose outcome ADO may or may not hold. A disposable copy would be erased by
        // exactly the crash it exists to survive.
        //
        // `published_id IS NULL` is the reconcilable state: an intent with no outcome. It is a
        // nullable column rather than a status enum so the open set is an index range, and so
        // there is no third state a writer could leave behind.
        //
        // title / type_name / recorded_at are the LOCAL disambiguation. The stamped ADO tag is a
        // single constant (PublishIntent.IntentTag) rather than a per-create GUID: a unique tag
        // per published item grows without bound against ADO's ~5,000 unique-tag project cap and
        // writes twig's private bookkeeping into a namespace shared with every human in the
        // project, which 0001 §1 forbids. So the tag narrows, and these three columns identify.
        // recorded_at is written BEFORE the call, so it is a valid lower bound on the created
        // item's System.CreatedDate — that is what stops a reused tag matching an older item.
        [3] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.publish_intents (
                staged_identity TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                type_name TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                published_id INTEGER,
                completed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_publish_intents_open
                ON publish_intents(published_id);
            """,
    };

    private bool SchemaExists()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='metadata';";
        return cmd.ExecuteScalar() is not null;
    }

    private bool SchemaVersionMatches()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        var result = cmd.ExecuteScalar();
        return result is string versionStr && int.TryParse(versionStr, out var version) && version == SchemaVersion;
    }

    private void DropAllTables()
    {
        // Table names are compile-time constants — not user-supplied values — so
        // string interpolation is safe here. SQLite does not support parameterised DDL identifiers.
        //
        // 0013: this list is the DISPOSABLE mirror only. Durable tables (pending_changes,
        // publish_id_map, seed_links) live in the attached `pending` schema and are NEVER
        // dropped — a SchemaVersion bump must not be able to reach them.
        string[] tables = ["work_items", "process_types", "context", "metadata", "field_definitions", "work_item_links", "navigation_history", "tracked_items", "excluded_items"];
        foreach (var table in tables)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS main.{table};";
            cmd.ExecuteNonQuery();
        }
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();

        // Default workspace mode to Sprint for new databases
        using var defaultCmd = _connection.CreateCommand();
        defaultCmd.CommandText = "INSERT OR IGNORE INTO context (key, value) VALUES ('workspace_mode', 'Sprint');";
        defaultCmd.ExecuteNonQuery();
    }

    private void WriteSchemaVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO metadata (key, value) VALUES ('schema_version', @version);";
        cmd.Parameters.AddWithValue("@version", SchemaVersion.ToString());
        cmd.ExecuteNonQuery();
    }

    private const string Ddl = """
        CREATE TABLE metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE work_items (
            id INTEGER PRIMARY KEY,
            type TEXT NOT NULL,
            title TEXT NOT NULL,
            state TEXT NOT NULL,
            parent_id INTEGER,
            assigned_to TEXT,
            iteration_path TEXT,
            area_path TEXT,
            revision INTEGER NOT NULL,
            is_seed INTEGER NOT NULL DEFAULT 0,
            seed_created_at TEXT,
            staged_identity TEXT,
            fields_json TEXT NOT NULL,
            is_dirty INTEGER NOT NULL DEFAULT 0,
            last_synced_at TEXT NOT NULL
        );

        CREATE TABLE process_types (
            type_name TEXT PRIMARY KEY,
            states_json TEXT NOT NULL,
            default_child_type TEXT,
            valid_child_types_json TEXT,
            color_hex TEXT,
            icon_id TEXT,
            last_synced_at TEXT NOT NULL
        );

        CREATE TABLE context (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE INDEX idx_work_items_type ON work_items(type);
        CREATE INDEX idx_work_items_parent ON work_items(parent_id);
        CREATE INDEX idx_work_items_iteration ON work_items(iteration_path);
        CREATE INDEX idx_work_items_assigned ON work_items(assigned_to);
        CREATE INDEX idx_work_items_dirty ON work_items(is_dirty) WHERE is_dirty = 1;
        CREATE INDEX idx_work_items_area ON work_items(area_path);
        CREATE INDEX idx_work_items_seed ON work_items(is_seed) WHERE is_seed = 1;

        CREATE TABLE field_definitions (
            ref_name TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            data_type TEXT NOT NULL,
            is_read_only INTEGER NOT NULL DEFAULT 0,
            last_synced_at TEXT NOT NULL
        );

        CREATE TABLE work_item_links (
            source_id INTEGER NOT NULL,
            target_id INTEGER NOT NULL,
            link_type TEXT NOT NULL,
            PRIMARY KEY (source_id, target_id, link_type)
        );
        CREATE INDEX idx_work_item_links_source ON work_item_links(source_id);

        CREATE TABLE navigation_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            work_item_id INTEGER NOT NULL,
            visited_at TEXT NOT NULL
        );

        CREATE TABLE tracked_items (
            id INTEGER PRIMARY KEY,
            mode TEXT NOT NULL DEFAULT 'single',
            created_at TEXT NOT NULL
        );

        CREATE TABLE excluded_items (
            id INTEGER PRIMARY KEY,
            created_at TEXT NOT NULL
        );
        """;

    public void Dispose()
    {
        _connection.Dispose();
    }
}
