using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence.ProviderTargets;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ISystemWorktreeRegistry"/>.
/// Stores the AB#736 §4.3 <c>system.db</c> at <c>~/.twig/system.db</c>.
/// WAL mode + <c>BEGIN IMMEDIATE</c> transactions per §6.2.
/// <para>
/// Unknown <c>layout_meta.version</c> values fail closed with
/// <c>system-store-schema-mismatch</c>. Only bounded additive v3/v4/v5/v6/v7/v8→v9
/// identity/native-migration/transition/remote-write/default-family upgrades followed
/// by v9→v10 provider-target storage are supported; earlier tuple-storage layouts
/// remain a hard boundary. Existing worktree, claim and credential authority is retained.
/// After a v9→v10 upgrade, binaries that only understand schema v9 fail closed; rolling
/// back the executable alone is unsupported and requires restoring a compatible database backup.
/// </para>
/// <para>
/// <b>Concurrent open safety.</b> Initialization is serialized across
/// every registry instance in this process through
/// <see cref="PathInitGates"/>, and across processes through SQLite's own
/// write lock + <c>BEGIN IMMEDIATE</c>. Each command owns its own
/// <see cref="SqliteConnection"/>-scoped transaction; the connection is
/// never used concurrently by two commands (the async
/// <see cref="_writeGate"/> plus the per-instance
/// <see cref="_connectionGate"/> maintain that). Shared-cache mode was
/// removed after review — the ADO.NET provider's shared-cache path threw
/// <see cref="ArgumentOutOfRangeException"/> under a specific concurrent
/// open pattern, and Twig gets nothing from shared cache the WAL-mode
/// path doesn't already give.
/// </para>
/// </summary>
internal sealed partial class SqliteSystemWorktreeRegistry : IDisposable
{
    // AB#739 bump from 1 → 2 for the tuple-storage schema change
    // (`primary_scope_kind` column + extended partial unique index).
    // Additive identity migrations: v3→v4 adds AAD bindings; v4→v5 adds
    // method-specific PAT principal evidence without rewriting AAD authority.
    // v5→v6 adds native recoverable connection migration intents, without rewriting existing rows.
    // v6→v7 adds recoverable binding pin/removal authority, retaining all prior ledgers.
    // v7→v8 adds native remote-write admission/observations/receipts; process death cannot erase uncertainty.
    // v8→v9 adds all-worktree default families; unfinished families fence every affected generation.
    // v9→v10 adds provider-neutral target bindings without referencing ADO rows.
    private const int SchemaVersion = 10;
    private const int OpenValidationRetryCount = 40;
    private const int OpenValidationRetryDelayMs = 25;

    private static readonly ConcurrentDictionary<string, object> PathInitGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _dbPath;
    private readonly TimeProvider _clock;
    private readonly object _connectionGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteConnection? _connection;
    private string? _openFailure;
    private bool _disposed;

    public SqliteSystemWorktreeRegistry(string dbPath, TimeProvider clock)
    {
        _dbPath = dbPath;
        _clock = clock;
    }

    public Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default)
        => ExecuteReadAsync<SystemWorktreeRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT connection_ref, retired_at FROM worktrees WHERE worktree_fingerprint = $fp LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return NativeResults.Missing<SystemWorktreeRow?>();
            var connectionRef = reader.GetString(0);
            DateTimeOffset? retiredAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)).ToUniversalTime();
            return NativeResults.Ok<SystemWorktreeRow?>(new SystemWorktreeRow(connectionRef, retiredAt));
        }, ct);

    public Task<Result<bool>> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO connections (connection_ref, organization, project, team, first_seen_at, last_seen_at)
VALUES ($ref, $org, $project, $team, $now, $now)
ON CONFLICT(connection_ref) DO UPDATE SET
    organization = excluded.organization,
    project = excluded.project,
    team = excluded.team,
    last_seen_at = excluded.last_seen_at;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$org", organization);
            cmd.Parameters.AddWithValue("$project", project);
            cmd.Parameters.AddWithValue("$team", (object?)team ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return NativeResults.Ok();
        }, ct);

    public Task<Result<bool>> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using (var fence = connection.CreateCommand())
            {
                fence.Transaction = tx;
                fence.CommandText = "SELECT 1 FROM connection_default_transitions WHERE state<>'completed' AND (connection_ref=$ref OR connection_ref=(SELECT connection_ref FROM worktrees WHERE worktree_fingerprint=$fp)) LIMIT 1;";
                fence.Parameters.AddWithValue("$ref", connectionRef);
                fence.Parameters.AddWithValue("$fp", worktreeFingerprint);
                if (await fence.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                    return NativeResults.Fail("binding-default-family-incomplete: registration is frozen until the exact native default intent completes.");
            }
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO worktrees (worktree_fingerprint, connection_ref, worktree_root, initialized_at, last_seen_at, retired_at)
VALUES ($fp, $ref, $root, $now, $now, NULL)
ON CONFLICT(worktree_fingerprint) DO UPDATE SET
    connection_ref = excluded.connection_ref,
    worktree_root = excluded.worktree_root,
    last_seen_at = excluded.last_seen_at,
    retired_at = NULL;";
            cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$root", worktreeRoot);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return NativeResults.Ok();
        }, ct);

    private async Task<Result<T>> ExecuteReadAsync<T>(Func<SqliteConnection, Task<Result<T>>> body, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return NativeResults.Fail<T>(openFailure!);
            return await body(connection!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return NativeResults.Fail<T>(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return NativeResults.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<Result<bool>> ExecuteWriteAsync(Func<SqliteConnection, SqliteTransaction, Task<Result<bool>>> body, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return NativeResults.Fail(openFailure!);
            using var tx = connection!.BeginTransaction(deferred: false);
            var result = await body(connection, tx).ConfigureAwait(false);
            if (result.IsSuccess) tx.Commit(); else tx.Rollback();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return NativeResults.Fail(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return NativeResults.Fail($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<Result<T>> ExecuteWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<Result<T>>> body, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return NativeResults.Fail<T>(openFailure!);
            using var tx = connection!.BeginTransaction(deferred: false);
            var result = await body(connection, tx).ConfigureAwait(false);
            if (result.IsSuccess) tx.Commit(); else tx.Rollback();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return NativeResults.Fail<T>(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return NativeResults.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
        finally
        {
            _writeGate.Release();
        }
    }

    private bool TryEnsureOpen(out SqliteConnection? connection, out string? failure)
    {
        // Fast path — this instance already has an open connection.
        lock (_connectionGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteSystemWorktreeRegistry));
            if (_openFailure is not null) { connection = null; failure = _openFailure; return false; }
            if (_connection is { State: System.Data.ConnectionState.Open }) { connection = _connection; failure = null; return true; }
        }

        // Slow path — first-time init. Serialize every registry instance in
        // the same process against the same DB path so two concurrent
        // instances cannot both race through PRAGMA + schema init on the
        // ADO.NET SqliteConnection (which threw ArgumentOutOfRangeException
        // under a specific concurrent-open pattern on Microsoft.Data.Sqlite).
        // Cross-process concurrency is still serialized by SQLite's own
        // write lock + BEGIN IMMEDIATE inside EnsureSchema.
        var canonical = Path.GetFullPath(_dbPath);
        var pathGate = PathInitGates.GetOrAdd(canonical, _ => new object());
        lock (pathGate)
        {
            // Re-check under the path gate — another instance in this
            // process may have opened this connection while we waited.
            lock (_connectionGate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SqliteSystemWorktreeRegistry));
                if (_openFailure is not null) { connection = null; failure = _openFailure; return false; }
                if (_connection is { State: System.Data.ConnectionState.Open }) { connection = _connection; failure = null; return true; }
            }

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var isNewDb = !File.Exists(_dbPath);

            // No shared-cache mode: multiple SqliteConnection instances in
            // the same process each get their own private cache. Every
            // command is transactionally scoped and every mutation goes
            // through BEGIN IMMEDIATE, so shared cache buys nothing here
            // but sidesteps the ArgumentOutOfRangeException path.
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                DefaultTimeout = 30,
            }.ToString();
            var newConnection = new SqliteConnection(cs);
            try
            {
                newConnection.Open();

                // PRAGMAs must run one at a time — Microsoft.Data.Sqlite's
                // batched PRAGMA path can misbehave under a mid-init race.
                // Retry SQLITE_BUSY (code 5) so a cross-process peer briefly
                // holding the write lock does not fail the whole open.
                ExecuteWithBusyRetry(newConnection, "PRAGMA journal_mode=WAL;");
                ExecuteWithBusyRetry(newConnection, "PRAGMA busy_timeout=30000;");
                ExecuteWithBusyRetry(newConnection, "PRAGMA foreign_keys=ON;");

                if (isNewDb)
                {
                    EnsureSchema(newConnection);
                }
                else
                {
                    // Existing DB — could be another peer's in-flight init.
                    // Wait for a committed layout_meta row, then either accept
                    // the current version, run a bounded additive migration
                    // from the previous version, or fail closed.
                    var (present, committedVersion) = WaitForCommittedSchemaVersion(newConnection);
                    if (!present)
                    {
                        _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                        newConnection.Dispose();
                        connection = null; failure = _openFailure; return false;
                    }
                    if (committedVersion != SchemaVersion)
                    {
                        if (!TryAdditiveMigrate(newConnection, committedVersion, SchemaVersion))
                        {
                            _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                            newConnection.Dispose();
                            connection = null; failure = _openFailure; return false;
                        }
                    }
                }
            }
            catch
            {
                newConnection.Dispose();
                throw;
            }

            lock (_connectionGate)
            {
                _connection = newConnection;
                connection = _connection; failure = null; return true;
            }
        }
    }

    private static void ExecuteWithBusyRetry(SqliteConnection conn, string sql)
    {
        const int maxAttempts = 60;
        SqliteException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
            {
                last = ex;
                Thread.Sleep(25);
            }
        }
        if (last is not null) throw last;
    }

    private static (bool present, int version) WaitForCommittedSchemaVersion(SqliteConnection connection)
    {
        for (var attempt = 0; attempt < OpenValidationRetryCount; attempt++)
        {
            var (present, version) = ProbeLayoutMeta(connection);
            if (present)
                return (true, version);
            Thread.Sleep(OpenValidationRetryDelayMs);
        }
        return (false, 0);
    }

    /// <summary>
    /// Forward-only additive upgrades through v9, then a separate v9→v10
    /// provider-target table addition. Existing connection/worktree/claim rows
    /// and credentials are never rewritten.
    /// </summary>
    private bool TryAdditiveMigrate(SqliteConnection connection, int fromVersion, int toVersion)
    {
        if (toVersion != SchemaVersion)
            return false;

        if (fromVersion is 3 or 4 or 5 or 6 or 7 or 8)
        {
            if (!TryAdditiveMigrateToNine(connection, fromVersion))
                return false;
            fromVersion = 9;
        }

        if (fromVersion != 9)
            return false;

        using var targetTx = connection.BeginTransaction(deferred: false);
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = targetTx;
                cmd.CommandText = ProviderTargetBindingSchemaSql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = targetTx;
                cmd.CommandText = "UPDATE layout_meta SET version = $version WHERE id = 1;";
                cmd.Parameters.AddWithValue("$version", toVersion);
                cmd.ExecuteNonQuery();
            }
            targetTx.Commit();
            return true;
        }
        catch
        {
            try { targetTx.Rollback(); } catch { /* best effort */ }
            return false;
        }
    }

    private static bool TryAdditiveMigrateToNine(SqliteConnection connection, int fromVersion)
    {
        using var tx = connection.BeginTransaction(deferred: false);
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = (fromVersion >= 5 ? string.Empty : IdentitySchemaSql + PatPrincipalSchemaSql)
                    + MigrationSchemaSql + BindingTransitionSchemaSql + RemoteWriteSchemaSql + DefaultTransitionSchemaSql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE layout_meta SET version = 9 WHERE id = 1;";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch
        {
            try { tx.Rollback(); } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>
    /// Shared DDL for the Task #1104 identity surface, used both by the
    /// initial-provision path in <see cref="EnsureSchema"/> and by the
    /// additive identity migrator. Pure <c>CREATE TABLE IF NOT EXISTS</c> and
    /// <c>CREATE INDEX IF NOT EXISTS</c> so running it against an
    /// already-migrated DB is a no-op.
    /// </summary>
    private const string IdentitySchemaSql = @"
CREATE TABLE IF NOT EXISTS identities (
    identity_id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    object_id TEXT NOT NULL,
    issuer TEXT NOT NULL,
    authority_host TEXT NOT NULL,
    credential_ref TEXT NOT NULL,
    account_name TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_identities_name ON identities(name);
CREATE UNIQUE INDEX IF NOT EXISTS idx_identities_credential_ref ON identities(credential_ref);
CREATE TABLE IF NOT EXISTS connection_bindings (
    binding_id TEXT PRIMARY KEY,
    connection_ref TEXT NOT NULL,
    identity_id TEXT NOT NULL REFERENCES identities(identity_id) ON DELETE RESTRICT,
    revision INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(connection_ref, identity_id)
);
CREATE INDEX IF NOT EXISTS idx_bindings_connection_ref ON connection_bindings(connection_ref);
CREATE TABLE IF NOT EXISTS connection_defaults (
    connection_ref TEXT PRIMARY KEY,
    binding_id TEXT NOT NULL REFERENCES connection_bindings(binding_id) ON DELETE RESTRICT,
    revision INTEGER NOT NULL,
    updated_at TEXT NOT NULL
);";

    private const string PatPrincipalSchemaSql = """
        CREATE TABLE IF NOT EXISTS pat_principals (
            identity_id TEXT PRIMARY KEY REFERENCES identities(identity_id) ON DELETE RESTRICT,
            authority TEXT NOT NULL,
            principal_id TEXT NOT NULL
        );
        """;

    private static (bool present, int version) ProbeLayoutMeta(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='layout_meta' LIMIT 1;";
            var exists = cmd.ExecuteScalar();
            if (exists is null) return (false, 0);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM layout_meta WHERE id = 1;";
            var raw = cmd.ExecuteScalar();
            if (raw is null || raw is DBNull) return (false, 0);
            try { return (true, Convert.ToInt32(raw)); }
            catch { return (true, -1); }
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction(deferred: false);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS layout_meta (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    version INTEGER NOT NULL,
    initialized_at TEXT NOT NULL,
    created_by TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS connections (
    connection_ref TEXT PRIMARY KEY,
    organization TEXT NOT NULL,
    project TEXT NOT NULL,
    team TEXT,
    first_seen_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS worktrees (
    worktree_fingerprint TEXT PRIMARY KEY,
    connection_ref TEXT NOT NULL REFERENCES connections(connection_ref) ON DELETE RESTRICT,
    worktree_root TEXT NOT NULL,
    initialized_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    retired_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_worktrees_connection_ref ON worktrees(connection_ref);
CREATE TABLE IF NOT EXISTS claims (
    claim_id TEXT PRIMARY KEY,
    connection_ref TEXT NOT NULL REFERENCES connections(connection_ref) ON DELETE RESTRICT,
    worktree_fingerprint TEXT NOT NULL REFERENCES worktrees(worktree_fingerprint) ON DELETE RESTRICT,
    primary_scope_kind TEXT NOT NULL,
    work_item_id INTEGER NOT NULL,
    state TEXT NOT NULL,
    cas_token TEXT NOT NULL,
    minted_at TEXT NOT NULL,
    ended_at TEXT,
    record_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_claims_worktree_fingerprint ON claims(worktree_fingerprint);
CREATE INDEX IF NOT EXISTS idx_claims_connection_kind_work_item ON claims(connection_ref, primary_scope_kind, work_item_id);
CREATE INDEX IF NOT EXISTS idx_claims_state ON claims(state);
CREATE UNIQUE INDEX IF NOT EXISTS idx_claims_unique_reserved
    ON claims(connection_ref, primary_scope_kind, work_item_id)
    WHERE state IN ('pending', 'active');
CREATE TABLE IF NOT EXISTS tuple_epochs (
    connection_ref TEXT NOT NULL,
    primary_scope_kind TEXT NOT NULL,
    work_item_id INTEGER NOT NULL,
    current_epoch INTEGER NOT NULL DEFAULT 0,
    winning_claim_id TEXT,
    winning_cas_token TEXT,
    PRIMARY KEY (connection_ref, primary_scope_kind, work_item_id)
);
CREATE TABLE IF NOT EXISTS profile_cache (
    connection_ref TEXT PRIMARY KEY REFERENCES connections(connection_ref) ON DELETE RESTRICT,
    profile_identity TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    payload TEXT NOT NULL,
    fetched_at TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }
        // Fresh provisioning and additive migration share the same identity DDL.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = IdentitySchemaSql + PatPrincipalSchemaSql + MigrationSchemaSql + BindingTransitionSchemaSql + RemoteWriteSchemaSql + DefaultTransitionSchemaSql + ProviderTargetBindingSchemaSql;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO layout_meta (id, version, initialized_at, created_by)
VALUES (1, $version, $now, 'twig-cli/system')
ON CONFLICT(id) DO NOTHING;";
            cmd.Parameters.AddWithValue("$version", SchemaVersion);
            cmd.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose()
    {
        lock (_connectionGate)
        {
            _connection?.Dispose();
            _connection = null;
            _disposed = true;
        }
        _writeGate.Dispose();
    }
}
