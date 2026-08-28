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
    internal const int SchemaVersion = 15;

    /// <summary>
    /// Schema version of the durable store (<c>pending.db</c>), versioned independently of
    /// <see cref="SchemaVersion"/>.
    /// <para>
    /// Unlike the mirror, the durable store is <b>never dropped and recreated</b> — it holds the
    /// only copy of work that ADO has never seen. Every future shape change here must be an
    /// additive migration in <see cref="DurableMigrations"/>, and this number bumped to match.
    /// </para>
    /// </summary>
    internal const int DurableSchemaVersion = 10;

    /// <summary>The schema name the durable store is ATTACHed under.</summary>
    internal const string DurableSchema = "pending";

    /// <summary>
    /// The durable schema version that introduced <c>active_context</c> (AB#688). Gating the
    /// one-off pointer rescue on the version that created the table keeps it running exactly
    /// once, even though the migration DDL itself is <c>IF NOT EXISTS</c>-idempotent.
    /// </summary>
    private const int ActiveContextMigration = 10;

    private readonly SqliteConnection _connection;
    private readonly TextWriter? _noticeWriter;
    private bool _schemaRebuilt;
    private bool _mirrorWasReset;
    private int? _resetFromVersion;
    private int _resetSeedCount;

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
    /// Opens (or creates) the SQLite database at the given connection string without a reset
    /// notice sink — equivalent to passing <see langword="null"/> to
    /// <see cref="SqliteCacheStore(string, TextWriter?)"/>.
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g., "Data Source=.twig/twig.db" or "Data Source=:memory:").</param>
    public SqliteCacheStore(string connectionString)
        : this(connectionString, null)
    {
    }

    /// <summary>
    /// Opens (or creates) the SQLite database at the given connection string.
    /// Enables WAL mode, attaches and migrates the durable store, then checks the mirror's
    /// schema version and creates/rebuilds its tables as needed. Wraps open in try-catch for
    /// corruption detection (FM-008).
    /// </summary>
    /// <param name="connectionString">SQLite connection string (e.g., "Data Source=.twig/twig.db" or "Data Source=:memory:").</param>
    /// <param name="noticeWriter">
    /// AB#688. Where a mirror <b>reset</b> announces itself. A reset is not silent-safe: it
    /// discards every cached work item, edge, verification marker and freshness stamp, so the
    /// user has to be told what went and how to get it back. <see langword="null"/> (the
    /// default overload) keeps the store quiet, which is what tests and benchmarks want.
    /// </param>
    public SqliteCacheStore(string connectionString, TextWriter? noticeWriter)
    {
        _noticeWriter = noticeWriter;
        _connection = new SqliteConnection(connectionString);
        try
        {
            _connection.Open();
            EnableWalMode();
            AttachDurableStore();
            // AB#688: the durable store is migrated BEFORE the mirror is touched, for two
            // reasons. (1) Durable migration 10 rescues the active-item pointer out of the
            // mirror's `context` table, which EnsureSchema is about to drop — after the drop
            // there is nothing left to rescue. (2) A durable migration that fails now fails
            // LOUDLY with the mirror still intact, rather than after it has been destroyed.
            // Durable migrations only ever write schema-qualified `pending.*` tables, so
            // running them against a not-yet-rebuilt mirror is safe.
            EnsureDurableSchema();
            EnsureSchema();
            AnnounceMirrorReset();
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
        if (SchemaExists() && SchemaVersionMatches())
            return;

        // AB#688. A rebuild is two different events wearing one name, and only one of them is
        // worth interrupting somebody about:
        //   CREATE — nothing was there, nothing is lost.
        //   RESET  — a populated mirror is about to be dropped on the floor.
        // Announcing a "your cache was discarded" warning on a first `twig init` would train
        // the reader to ignore the one message that matters, so the distinction is drawn here
        // and carried to AnnounceMirrorReset.
        _mirrorWasReset = MirrorHasAnyTable();
        _resetFromVersion = _mirrorWasReset ? ReadMirrorSchemaVersion() : null;
        // Counted BEFORE the drop, because afterwards there is nothing left to count. Seed
        // records live only in the mirror's work_items, so a reset destroys local work ADO has
        // never seen and `twig sync` cannot rebuild — the one loss here with no recovery path.
        _resetSeedCount = _mirrorWasReset ? CountMirrorSeeds() : 0;

        GuardLegacyPendingSet();
        DropAllTables();
        DropLegacyDurableTables();
        CreateSchema();
        WriteSchemaVersion();
        _schemaRebuilt = true;
    }

    /// <summary>
    /// Whether the mirror held anything at all before the rebuild. Deliberately broader than
    /// <see cref="SchemaExists"/>: a mirror whose <c>metadata</c> table went missing but whose
    /// <c>work_items</c> rows survived still loses real data on rebuild.
    /// </summary>
    private bool MirrorHasAnyTable()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT 1 FROM main.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' LIMIT 1;";
        return cmd.ExecuteScalar() is not null;
    }

    private int? ReadMirrorSchemaVersion()
    {
        if (!SchemaExists())
            return null;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        return cmd.ExecuteScalar() is string raw && int.TryParse(raw, out var version) ? version : null;
    }

    /// <summary>
    /// AB#688. Says out loud that the mirror was reset, what went with it, and how to get it
    /// back.
    /// <para>
    /// The reset used to be entirely silent. <see cref="SchemaWasRebuilt"/> existed but was read
    /// by no production code — and could not usefully be: twig builds a throwaway
    /// <see cref="SqliteCacheStore"/> during startup to hydrate the theme, so on an upgraded
    /// workspace the rebuild happens on an instance that is disposed before any command runs and
    /// the injected store truthfully reports <see langword="false"/>. Announcing at the moment of
    /// the reset is the only placement that cannot miss it.
    /// </para>
    /// </summary>
    private void AnnounceMirrorReset()
    {
        if (!_mirrorWasReset || _noticeWriter is null)
            return;

        var from = _resetFromVersion?.ToString() ?? "an older version";
        _noticeWriter.WriteLine(
            $"\u26a0 The twig cache was rebuilt for a new schema ({from} \u2192 {SchemaVersion}).");
        _noticeWriter.WriteLine(
            "  Discarded: cached work items, links, link verification markers, navigation history, and cache freshness.");

        if (_resetSeedCount > 0)
            _noticeWriter.WriteLine(
                $"  \u26a0 {_resetSeedCount} unpublished seed(s) went with it. ADO has never seen them, " +
                "so 'twig sync' cannot bring them back.");

        // The kept list is read back from the durable store rather than asserted from a fixed
        // sentence. A hard-coded list is a claim nobody re-checks, and it is exactly how this
        // ticket's own bug reads one layer up: confidently reporting something you did not look at.
        var active = ReadDurableActiveWorkItemId();
        _noticeWriter.WriteLine(active is int id
            ? $"  Kept: the active work item (#{id}), pending changes, benches, and the change-proposal journal."
            : "  Kept: pending changes, benches, and the change-proposal journal.");

        _noticeWriter.WriteLine("  Run 'twig sync' to repopulate the cache.");

        if (active is null)
            _noticeWriter.WriteLine("  No active work item survived — set one again with 'twig set <id>'.");
    }

    /// <summary>
    /// How many unpublished seeds the mirror is about to lose.
    /// <para>
    /// Shape-guarded for the same reason the pointer rescue is: a partial or hand-built mirror
    /// can carry a <c>work_items</c> table with no <c>is_seed</c> column, and SQLite fails to
    /// PREPARE against a missing column — which would turn a diagnostic count into a failure to
    /// open the cache at all.
    /// </para>
    /// </summary>
    private int CountMirrorSeeds()
    {
        if (!MirrorTableExists("work_items"))
            return 0;

        using (var shape = _connection.CreateCommand())
        {
            shape.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('work_items') WHERE name = 'is_seed';";
            if (Convert.ToInt32(shape.ExecuteScalar()) != 1)
                return 0;
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM main.work_items WHERE is_seed = 1;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int? ReadDurableActiveWorkItemId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT work_item_id FROM {DurableSchema}.active_context WHERE id = 1;";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value);
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
        if (!MirrorTableExists("pending_changes"))
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

    private bool MirrorTableExists(string table)
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

            // AB#688. The pointer rescue cannot live in the migration SQL: on a brand-new
            // database the mirror's `context` table does not exist yet, and SQLite fails to
            // PREPARE a statement naming a missing table, so the whole migration batch would
            // throw before creating anything. It is therefore a guarded step, inside the same
            // transaction, gated on the pre-migration version so it runs exactly once.
            if (from < ActiveContextMigration)
                RescueActiveWorkItemPointer(tx);

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
    /// AB#688. Carries the active-item pointer across the mirror/durable split, once.
    /// <para>
    /// Runs inside <see cref="EnsureDurableSchema"/>'s transaction and before
    /// <see cref="EnsureSchema"/> drops the mirror, so the value is read while it still exists.
    /// A store with no mirror <c>context</c> table — or with a same-named table of a shape this
    /// rescue does not recognise — has nothing to carry and quietly does nothing.
    /// </para>
    /// </summary>
    private void RescueActiveWorkItemPointer(SqliteTransaction tx)
    {
        if (!MirrorContextTableCarriesKeyValue())
            return;

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        // INSERT OR IGNORE, not REPLACE: a durable pointer already written by this binary is
        // newer than anything left in the mirror, and must win.
        cmd.CommandText = $"""
            INSERT OR IGNORE INTO {DurableSchema}.active_context (id, work_item_id, set_at)
            SELECT 1, CAST(value AS INTEGER), @now
            FROM main.context
            WHERE key = 'active_work_item_id' AND CAST(value AS INTEGER) <> 0;
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether the mirror's <c>context</c> table is really the key/value table this rescue reads.
    /// <para>
    /// A pre-rebuild mirror is not guaranteed to hold <i>our</i> shape: a partially-created or
    /// hand-built database can carry a <c>context</c> table with only a <c>key</c> column, and
    /// SQLite fails to PREPARE a statement naming a missing column. Without this check, a
    /// best-effort pointer rescue turns into a hard failure to open the cache at all — the exact
    /// class of collateral damage AB#688 is about.
    /// </para>
    /// </summary>
    private bool MirrorContextTableCarriesKeyValue()
    {
        if (!MirrorTableExists("context"))
            return false;

        // Unqualified pragma_table_info resolves against main, which is the schema in question;
        // there is no `pending.context` for it to pick up instead.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('context') WHERE name IN ('key', 'value');";
        return Convert.ToInt32(cmd.ExecuteScalar()) == 2;
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

        // ADO #144 / wayfinder 1007, docs/specs/bench.spec.md §1. The Bench.
        //
        // WHY DURABLE by 0005's test ("can ADO rebuild it?"): no. A Bench is an arrangement the
        // person built by hand — pins ADO has never heard of, and a name only they chose. A
        // disposable copy would be erased by a SchemaVersion bump, and pins are SILENT: nothing
        // prompts and nothing refuses, so the loss surfaces weeks later. This is the first NEW
        // durable table since the store split and can never be dropped-and-recreated.
        //
        // `selector_kind` is TEXT and `selector_payload` is an opaque per-kind blob rather than a
        // column per kind, because spec §2 requires the model to admit further selector kinds
        // WITHOUT a schema change. A boolean column per kind would make every new kind a
        // migration against a table that can never be rebuilt.
        //
        // 🔴 There is NO ordinal column, and that absence is load-bearing. Membership is the
        // UNION of a Bench's selectors and order does not matter (spec, Solution). Storing a
        // position would invite an implementation to evaluate in sequence, which passes every
        // other test while silently making two Benches with identical selectors behave
        // differently by construction order. The UNIQUE constraint carries the other half: the
        // same selector added twice is one row, so overlap cannot duplicate.
        //
        // `is_default` marks the one Bench twig creates on its own (spec §4). It is a column
        // rather than a reserved name so the default is an ordinary row of the same mechanism —
        // if the default were special-cased, it would not be a Bench and the parity bar would be
        // met by a fiction.
        [4] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.benches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS {DurableSchema}.idx_benches_name
                ON benches(name COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS {DurableSchema}.idx_benches_default
                ON benches(is_default) WHERE is_default = 1;

            CREATE TABLE IF NOT EXISTS {DurableSchema}.bench_selectors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                bench_id INTEGER NOT NULL REFERENCES benches(id) ON DELETE CASCADE,
                selector_kind TEXT NOT NULL,
                selector_payload TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_bench_selectors_bench
                ON bench_selectors(bench_id);
            CREATE UNIQUE INDEX IF NOT EXISTS {DurableSchema}.idx_bench_selectors_unique
                ON bench_selectors(bench_id, selector_kind, selector_payload);
            """,

        // ADO #149 / docs/specs/bench.spec.md §5. WHICH Bench is current.
        //
        // WHY DURABLE by 0005's test ("can ADO rebuild it?"): no. Which arrangement the person is
        // standing on is theirs, and ADO has never heard of it. It also must not be droppable: a
        // SchemaVersion bump that silently moved somebody back to the default Bench would look
        // exactly like the "a name always resolves, so it resolves to the WRONG thing" failure
        // family this change exists to escape.
        //
        // 🔴 This is a SEPARATE slot from IContextStore's active_work_item_id, deliberately. That
        // one is Context work on its own schedule; absorbing it here would make both changes
        // harder to review and would couple "which item am I on" to "which arrangement am I at".
        //
        // Single row, pinned by `CHECK (id = 1)`: "the current Bench" is one fact, and a table
        // that could hold two rows would need a rule elsewhere deciding which one won.
        //
        // `bench_id` is NULLABLE with ON DELETE SET NULL rather than a hard FK to a row that must
        // exist. Deleting the current Bench (#150) then leaves NULL — meaning "the default" —
        // instead of a dangling id that would resolve to nothing or, worse, to whatever row later
        // reused the number. A missing pointer has ONE meaning and it is the safe one.
        [5] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.current_bench (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                bench_id INTEGER REFERENCES benches(id) ON DELETE SET NULL,
                switched_at TEXT NOT NULL
            );
            """,

        // Twig plan native — foundational storage for a declarative Plan document. The plan
        // file is bound to its canonical SHA-256 digest (the primary key here), and the journal
        // is the DURABLE side of the "record intent before the call, record the outcome after
        // it" contract (0001 §4). ADO has never heard of it, so a mirror rebuild must not be
        // able to reach it — hence this schema.
        //
        // Two tables, one relationship. plan_journals is the header the source file digest maps
        // to; plan_operations is the per-op ledger. Together they give strict crash recovery:
        // reopening the store observes exactly the last committed state of each operation, so
        // apply can resume from wherever a previous run halted.
        //
        // 🔴 ORDINAL: the plan file lists operations in a definite order, and apply MUST walk
        // them in that order. Ordinal is that order, stored explicitly and enforced UNIQUE per
        // journal. The PRIMARY KEY is (digest, op_id) because op_id is the caller-facing
        // identifier; (digest, ordinal) is the ordering key.
        //
        // 🔴 STATE: mirrored between the header and each op, and each is authoritative for its
        // scope. The header's state is the plan-level lifecycle (Planned → Confirmed → …); each
        // operation's state advances independently under the atomic compare-and-transition
        // guard implemented by SqlitePlanJournalRepository.TryTransitionOperationAsync. The
        // source file NEVER stores statuses — states live only here.
        //
        // FK is declared for documentation and future enforcement; SQLite honours it only when
        // PRAGMA foreign_keys is on, matching the existing bench_selectors precedent.
        [6] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.plan_journals (
                digest TEXT PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                organization TEXT NOT NULL,
                project TEXT NOT NULL,
                source_path TEXT NOT NULL,
                canonical_json TEXT NOT NULL,
                state TEXT NOT NULL,
                previewed_at TEXT NOT NULL,
                confirmed_at TEXT,
                completed_at TEXT,
                error TEXT
            );
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_plan_journals_state
                ON plan_journals(state);

            CREATE TABLE IF NOT EXISTS {DurableSchema}.plan_operations (
                digest TEXT NOT NULL REFERENCES plan_journals(digest) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                op_id TEXT NOT NULL,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                request_json TEXT NOT NULL,
                started_at TEXT,
                applied_at TEXT,
                verified_at TEXT,
                result_json TEXT,
                error TEXT,
                PRIMARY KEY (digest, op_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS {DurableSchema}.idx_plan_operations_ordinal
                ON plan_operations(digest, ordinal);
            CREATE INDEX IF NOT EXISTS {DurableSchema}.idx_plan_operations_state
                ON plan_operations(state);
            """,

        // AB#754 / spec #753 — warning detail alongside a Verified plan operation.
        //
        // WHY A COLUMN AND NOT A STATE: `Verified` remains the SOLE landed-success state. A
        // post-PATCH readback can now prove the intended mutation landed while ADO has
        // rewritten a field its own revision machinery owns (ClosedDate/ClosedBy,
        // ChangedDate/ChangedBy, StateChangeDate). That is a successful apply with a caveat,
        // not a fourth outcome, so the caveat is a nullable column on the existing row.
        //
        // WHY NOT REUSE `error`: every consumer treats a non-null error as a failed operation.
        // Writing a warning there would make a Verified row read as failed to CLI, MCP, and
        // the header-completion scan that propagates the first row error.
        //
        // Additive ALTER, per this store's never-dropped contract. Existing rows get NULL,
        // which is exactly "no normalization observed".
        [7] = $"""
            ALTER TABLE {DurableSchema}.plan_operations ADD COLUMN warning TEXT;
            """,
        // AB#742 / design record T2 — settled "Change Proposal" vocabulary. This is a pure
        // RENAME: `plan_journals` becomes `proposal_journals` and `plan_operations` becomes
        // `proposal_operations`.
        //
        // 🔴 ROW-PRESERVING BY CONTRACT. The durable store is never dropped, and the rows
        // in these tables are the ONLY record of intents twig recorded before an ADO call
        // and outcomes it observed after — the "record intent before, record outcome after"
        // contract from 0001 §4. A migration that recreated these tables would silently
        // destroy real audit history that ADO cannot rebuild. Every subsequent statement
        // below either renames in place or rebuilds an index; not one drops a data row.
        //
        // WHY `ALTER TABLE ... RENAME TO` AND NOT create-new + INSERT SELECT + drop-old:
        // SQLite defaults `legacy_alter_table = OFF` since 3.25 (2018), and the bundle this
        // repo pins — SQLitePCLRaw.bundle_e_sqlite3 2.1.11 under Microsoft.Data.Sqlite
        // 10.0.6 — ships a modern engine well past that. With legacy_alter_table OFF, the
        // rename automatically rewrites `REFERENCES plan_journals(digest) ON DELETE CASCADE`
        // in the child table to point at the new parent name, preserving the cascade FK
        // the operations ledger relies on. No explicit rebuild of the FK is needed.
        //
        // Indexes follow the renamed table but keep their OLD names, so they are dropped
        // and re-created against the new tables with the settled names. The unique
        // (digest, ordinal) constraint carries over unchanged.
        [8] = $"""
            ALTER TABLE {DurableSchema}.plan_journals RENAME TO proposal_journals;
            ALTER TABLE {DurableSchema}.plan_operations RENAME TO proposal_operations;

            DROP INDEX {DurableSchema}.idx_plan_journals_state;
            DROP INDEX {DurableSchema}.idx_plan_operations_ordinal;
            DROP INDEX {DurableSchema}.idx_plan_operations_state;

            CREATE INDEX {DurableSchema}.idx_proposal_journals_state
                ON proposal_journals(state);
            CREATE UNIQUE INDEX {DurableSchema}.idx_proposal_operations_ordinal
                ON proposal_operations(digest, ordinal);
            CREATE INDEX {DurableSchema}.idx_proposal_operations_state
                ON proposal_operations(state);
            """,

        // AB#743 / design record T2 §5.3 — the audit columns an applied Change Proposal is
        // journaled with: who authorized it, in which mode, on what rationale, what they were
        // shown, and when.
        //
        // 🔴 EVERY COLUMN IS NULLABLE, AND THAT IS THE DESIGN. The durable store is never
        // dropped, so rows already in this table are real audit history from before
        // authorization was recorded at all. A NOT NULL column with a backfilled default would
        // manufacture an authorization that never happened — the single worst thing an audit
        // migration can do. A reader MUST therefore treat NULL here as "predates authorization
        // recording", NEVER as "unauthorized".
        //
        // WHY `review_model_json` IS SEPARATE FROM `canonical_json`: they answer different
        // questions. `canonical_json` is WHAT WAS AUTHORIZED — the digest-bound proposal.
        // `review_model_json` is WHAT THE AUTHORIZER WAS SHOWN — the derived semantic model,
        // including live board context that is deliberately not part of the digest. Spec #729's
        // audit goal is to reconstruct what happened without replaying the tool, and that needs
        // both: the proposal alone cannot show what the reviewer saw, and the review model alone
        // is not what the apply was bound to.
        //
        // `authorization_mode` holds the closed set `human|model`. It is NOT the HITL/AFK
        // vocabulary of `Custom.WayfinderExecutionMode`, which describes a session rather than
        // an apply.
        [9] = $"""
            ALTER TABLE {DurableSchema}.proposal_journals ADD COLUMN authorization_mode TEXT;
            ALTER TABLE {DurableSchema}.proposal_journals ADD COLUMN authorizer_identity TEXT;
            ALTER TABLE {DurableSchema}.proposal_journals ADD COLUMN rationale TEXT;
            ALTER TABLE {DurableSchema}.proposal_journals ADD COLUMN review_model_json TEXT;
            ALTER TABLE {DurableSchema}.proposal_journals ADD COLUMN authorized_at TEXT;
            """,

        // AB#688 — the active-item pointer moves into the store a SchemaVersion bump cannot
        // reach.
        //
        // WHY DURABLE by 0005's test ("can ADO rebuild it?"): no. Which item the person is
        // standing on is theirs; ADO has never heard of it. It is the same class of fact as
        // `current_bench`, and migration 5 above already argued this exact case — then left
        // `active_work_item_id` alone as "Context work on its own schedule". This is that
        // schedule. Leaving it in the mirror made a `twig set` silently evaporate mid-session
        // on the very SchemaVersion bump that shipped AB#831, with no warning and no hint.
        //
        // 🔴 ONLY THE POINTER MOVES, and the rest of `context` stays disposable on purpose.
        // `last_refreshed_at` and the navigation cursor describe the mirror, so they MUST die
        // with it: a freshness stamp that outlived the data it describes would report a current
        // cache while the cache is empty — the same "answers confidently about something it does
        // not know" failure AB#831 just removed from the link cache. Durability is not a reward
        // for being useful; it is an answer to "can ADO rebuild it?".
        //
        // Single row pinned by `CHECK (id = 1)`, matching `current_bench`: "the active item" is
        // one fact, and a table that could hold two rows would need a rule elsewhere to decide
        // which one wins. `work_item_id` is NULLABLE so that clearing the pointer is a value
        // rather than a missing row a reader has to interpret.
        [10] = $"""
            CREATE TABLE IF NOT EXISTS {DurableSchema}.active_context (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                work_item_id INTEGER,
                set_at TEXT NOT NULL
            );
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
        // ADO #151: tracked_items and excluded_items are GONE. They were declared, dropped, and
        // read by nothing — a grep found them and told the reader a false story about where pins
        // live, which is exactly the premise the Bench build brief inherited and got wrong. Pins
        // are selectors on a Bench in the durable store (#145/#146); exclusions live in the
        // tracking file and are deliberately outside the Bench.
        string[] tables = ["work_items", "process_types", "context", "metadata", "field_definitions", "work_item_links", "work_item_link_verifications", "navigation_history", "iteration_calendar"];
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
            -- AB#656. Reference names of every category this type belongs to, from
            -- _apis/wit/workitemtypecategories. A JSON array because the relation is
            -- many-to-many; Microsoft.HiddenCategory membership is what marks a type as
            -- ADO tooling rather than user-creatable vocabulary. NULL predates the column.
            category_reference_names_json TEXT,
            last_synced_at TEXT NOT NULL
        );

        CREATE TABLE context (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- ADO #144. The local mapping from an iteration path to the span of time it covers.
        --
        -- A sprint is a NAME mapped to a date range: the path is the stable identity, the dates
        -- are an attribute that can be moved. So a Bench stores the rule ("the iteration covering
        -- today") and this table answers WHICH iteration that is, from local data plus the local
        -- clock — never a network call, which is what lets a Bench evaluate offline.
        --
        -- In the DISPOSABLE mirror by 0005's test: ADO can rebuild it, because it is a copy of
        -- ADO's own iteration list. The refresh path repopulates it when twig already has a
        -- connection.
        CREATE TABLE iteration_calendar (
            path TEXT PRIMARY KEY,
            start_date TEXT,
            end_date TEXT
        );

        CREATE INDEX idx_iteration_calendar_range ON iteration_calendar(start_date, end_date);

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

        -- AB#831. `work_item_links` cannot answer "does this item have no edges, or has nobody
        -- ever asked?" — both are zero rows. That ambiguity is the bug: a cache-only read
        -- returned `links: []` for an item with live Predecessor edges and no consumer could
        -- tell it apart from a genuinely isolated item.
        --
        -- One row per SOURCE id whose whole edge set has been read from ADO and written to
        -- `work_item_links`. Written by SqliteWorkItemLinkRepository.SaveLinksAsync in the same
        -- transaction that replaces the edge set, INCLUDING when that set is empty — an
        -- empty-but-verified edge set is precisely the case a bare row count cannot express.
        CREATE TABLE work_item_link_verifications (
            source_id INTEGER PRIMARY KEY,
            verified_at TEXT NOT NULL
        );

        CREATE TABLE navigation_history (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            work_item_id INTEGER NOT NULL,
            visited_at TEXT NOT NULL
        );

        """;

    public void Dispose()
    {
        _connection.Dispose();
    }
}
