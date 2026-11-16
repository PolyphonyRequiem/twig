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
/// <c>system-store-schema-mismatch</c>. Only fresh initialization and exact-current
/// schema reopen are supported. Unsupported stores are never upgraded or reset.
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
    internal const int SchemaVersion = 13;
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

                if (!isNewDb)
                {
                    var (present, committedVersion) = WaitForCommittedSchemaVersion(newConnection);
                    if (!present || committedVersion != SchemaVersion)
                    {
                        _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                        newConnection.Dispose();
                        connection = null; failure = _openFailure; return false;
                    }
                    using var validation = newConnection.BeginTransaction(deferred: true);
                    var gate = ReadCoreGate(newConnection, validation, CancellationToken.None).GetAwaiter().GetResult();
                    if (!gate.IsSuccess)
                        throw new InvalidDataException(gate.Error.Message);
                    CheckCaptureReferences(newConnection, validation, CancellationToken.None).GetAwaiter().GetResult();
                    using var births = newConnection.CreateCommand();
                    births.Transaction = validation;
                    births.CommandText = "SELECT id,store_instance,origin,source,ownership,registry_instance,registry_revision,origin_version,parent_digest,covered,born,enlisted FROM core_scope_births LIMIT 0;";
                    births.ExecuteScalar();
                }

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
            }
            catch (InvalidDataException)
            {
                newConnection.Dispose();
                _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                connection = null; failure = _openFailure; return false;
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
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='layout_meta';";
            if (Convert.ToInt32(probe.ExecuteScalar()) != 0)
            {
                probe.CommandText = "SELECT version FROM layout_meta WHERE id=1;";
                if (Convert.ToInt32(probe.ExecuteScalar()) != SchemaVersion)
                    throw new InvalidDataException("A concurrent native initializer committed another schema; no downgrade is allowed.");
                CheckCaptureReferences(connection, tx, CancellationToken.None).GetAwaiter().GetResult();
                tx.Commit();
                return;
            }
        }
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
";
            cmd.ExecuteNonQuery();
        }
        // Provision the complete current schema in the same transaction.
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = IdentitySchemaSql + PatPrincipalSchemaSql + ActivationSchemaSql + BindingTransitionSchemaSql + RemoteWriteSchemaSql + DefaultTransitionSchemaSql + ProviderTargetBindingSchemaSql + CoreExecutionSchemaSql + CaptureSchemaSql + ScopeBirthSchemaSql;
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
