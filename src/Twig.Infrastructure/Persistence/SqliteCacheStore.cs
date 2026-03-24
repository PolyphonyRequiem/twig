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
    internal const int SchemaVersion = 8;

    private readonly SqliteConnection _connection;
    private bool _schemaRebuilt;

    static SqliteCacheStore()
    {
        SQLitePCL.Batteries.Init();
    }

    /// <summary>
    /// Opens (or creates) the SQLite database at the given connection string.
    /// Enables WAL mode, checks schema version, and creates/rebuilds tables as needed.
    /// Wraps open in try-catch for corruption detection (FM-008).
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g., "Data Source=.twig/twig.db" or "Data Source=:memory:").</param>
    public SqliteCacheStore(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            EnableWalMode();
            EnsureSchema();
        }
        catch (SqliteException ex)
        {
            _connection.Dispose();
            // I-003: Preserve the original exception chain for debugging
            throw new InvalidOperationException(
                "\u26a0 Cache corrupted. Run 'twig init --force' to rebuild.",
                ex);
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

    private void EnsureSchema()
    {
        if (!SchemaExists() || !SchemaVersionMatches())
        {
            DropAllTables();
            CreateSchema();
            WriteSchemaVersion();
            _schemaRebuilt = true;
        }
    }

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
        string[] tables = ["pending_changes", "work_items", "process_types", "context", "metadata", "field_definitions", "work_item_links", "seed_links", "publish_id_map"];
        foreach (var table in tables)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS {table};";
            cmd.ExecuteNonQuery();
        }
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
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
            fields_json TEXT NOT NULL,
            is_dirty INTEGER NOT NULL DEFAULT 0,
            last_synced_at TEXT NOT NULL
        );

        CREATE TABLE pending_changes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            work_item_id INTEGER NOT NULL,
            change_type TEXT NOT NULL,
            field_name TEXT,
            old_value TEXT,
            new_value TEXT,
            created_at TEXT NOT NULL,
            FOREIGN KEY (work_item_id) REFERENCES work_items(id)
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
        CREATE INDEX idx_work_items_seed ON work_items(is_seed) WHERE is_seed = 1;
        CREATE INDEX idx_pending_changes_item ON pending_changes(work_item_id);

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

        CREATE TABLE seed_links (
            source_id INTEGER NOT NULL,
            target_id INTEGER NOT NULL,
            link_type TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (source_id, target_id, link_type)
        );
        CREATE INDEX idx_seed_links_source ON seed_links(source_id);
        CREATE INDEX idx_seed_links_target ON seed_links(target_id);

        CREATE TABLE publish_id_map (
            old_id INTEGER PRIMARY KEY,
            new_id INTEGER NOT NULL,
            published_at TEXT NOT NULL
        );
        """;

    public void Dispose()
    {
        _connection.Dispose();
    }
}
