using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ISystemWorktreeRegistry"/>.
/// Stores the AB#736 §4.3 <c>system.db</c> at <c>~/.twig/system.db</c>.
/// WAL mode + <c>BEGIN IMMEDIATE</c> transactions per §6.2.
/// <para>
/// On open the store distinguishes a truly new database from an existing
/// one: if the file existed before this process opened it, the layout_meta
/// row is exact-matched against <see cref="SchemaVersion"/> BEFORE any DDL
/// runs, and a mismatch surfaces <c>system-store-schema-mismatch</c>
/// permanently. Only a genuinely new file triggers schema initialization —
/// existing databases are inspected read-only until validation passes.
/// </para>
/// </summary>
internal sealed class SqliteSystemWorktreeRegistry : ISystemWorktreeRegistry, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _dbPath;
    private readonly TimeProvider _clock;
    private readonly object _connectionGate = new();
    private SqliteConnection? _connection;
    private string? _openFailure;
    private bool _disposed;

    public SqliteSystemWorktreeRegistry(string dbPath, TimeProvider clock)
    {
        _dbPath = dbPath;
        _clock = clock;
    }

    public Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default)
        => ExecuteAsync<SystemWorktreeRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT connection_ref, retired_at FROM worktrees WHERE worktree_fingerprint = $fp LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemWorktreeRow?>(null);
            var connectionRef = reader.GetString(0);
            DateTimeOffset? retiredAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)).ToUniversalTime();
            return Result.Ok<SystemWorktreeRow?>(new SystemWorktreeRow(connectionRef, retiredAt));
        }, ct);

    public Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default)
        => ExecuteWriteAsync(async connection =>
        {
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
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
            return Result.Ok();
        }, ct);

    public Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
        => ExecuteWriteAsync(async connection =>
        {
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
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
            return Result.Ok();
        }, ct);

    public Task<Result> InsertClaimAsync(
        string claimId, string connectionRef, string worktreeFingerprint,
        int workItemId, string state, string casToken, string recordJson, CancellationToken ct = default)
        => ExecuteWriteAsync(async connection =>
        {
            // FK precheck — return a named error instead of the opaque
            // constraint failure the raw INSERT would raise.
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT 1 FROM worktrees WHERE worktree_fingerprint = $fp LIMIT 1;";
                check.Parameters.AddWithValue("$fp", worktreeFingerprint);
                var exists = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (exists is null)
                    return Result.Fail(AttachmentStorageFailure.WorktreeNotRegistered);
            }

            var now = _clock.GetUtcNow().ToString("o");
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
INSERT INTO claims (claim_id, connection_ref, worktree_fingerprint, work_item_id, state, cas_token, minted_at, ended_at, record_json)
VALUES ($id, $ref, $fp, $wi, $state, $tok, $now, NULL, $json);";
                cmd.Parameters.AddWithValue("$id", claimId);
                cmd.Parameters.AddWithValue("$ref", connectionRef);
                cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
                cmd.Parameters.AddWithValue("$wi", workItemId);
                cmd.Parameters.AddWithValue("$state", state);
                cmd.Parameters.AddWithValue("$tok", casToken);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$json", recordJson);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return Result.Ok();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                // Either the (connection_ref, work_item_id) partial unique
                // index tripped — another pending/active row exists — or the
                // primary key collided. The AB#739 caller decides which is
                // fatal; storage carries the named identifier and the
                // low-level message.
                var msg = ex.Message ?? string.Empty;
                var code = msg.Contains("idx_claims_unique_reserved", StringComparison.Ordinal)
                    ? AttachmentStorageFailure.ClaimDuplicateReserved
                    : AttachmentStorageFailure.ClaimDuplicateReserved;
                return Result.Fail($"{code}: {msg}");
            }
        }, ct);

    public Task<Result> UpdateClaimStateAsync(
        string claimId, string expectedCasToken, string newCasToken,
        string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default)
        => ExecuteWriteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
UPDATE claims
   SET state = $state,
       cas_token = $newTok,
       ended_at = $endedAt,
       record_json = $json
 WHERE claim_id = $id AND cas_token = $expTok;";
            cmd.Parameters.AddWithValue("$id", claimId);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$expTok", expectedCasToken);
            cmd.Parameters.AddWithValue("$newTok", newCasToken);
            cmd.Parameters.AddWithValue("$endedAt", (object?)endedAt?.ToUniversalTime().ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$json", recordJson);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                return Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
            return Result.Ok();
        }, ct);

    public Task<Result<SystemClaimRow?>> FindClaimAsync(string claimId, CancellationToken ct = default)
        => ExecuteAsync<SystemClaimRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT claim_id, connection_ref, worktree_fingerprint, work_item_id, state, cas_token, minted_at, ended_at, record_json FROM claims WHERE claim_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", claimId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemClaimRow?>(null);
            return Result.Ok<SystemClaimRow?>(ReadClaimRow(reader));
        }, ct);

    public async Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default)
    {
        if (reservedStates.Count == 0)
            return Result.Ok<SystemClaimRow?>(null);
        return await ExecuteAsync<SystemClaimRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            var placeholders = new List<string>(reservedStates.Count);
            for (var i = 0; i < reservedStates.Count; i++)
            {
                var pname = $"$s{i}";
                placeholders.Add(pname);
                cmd.Parameters.AddWithValue(pname, reservedStates[i]);
            }
            cmd.CommandText = $@"
SELECT claim_id, connection_ref, worktree_fingerprint, work_item_id, state, cas_token, minted_at, ended_at, record_json
  FROM claims
 WHERE connection_ref = $ref
   AND work_item_id = $wi
   AND state IN ({string.Join(", ", placeholders)})
 LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemClaimRow?>(null);
            return Result.Ok<SystemClaimRow?>(ReadClaimRow(reader));
        }, ct).ConfigureAwait(false);
    }

    public Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default)
        => ExecuteAsync<SystemProfileCacheRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT connection_ref, profile_identity, profile_version, payload, fetched_at FROM profile_cache WHERE connection_ref = $ref LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemProfileCacheRow?>(null);
            return Result.Ok<SystemProfileCacheRow?>(new SystemProfileCacheRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)).ToUniversalTime()));
        }, ct);

    public Task<Result> WriteProfileCacheAsync(string connectionRef, string profileIdentity, string profileVersion, string payload, CancellationToken ct = default)
        => ExecuteWriteAsync(async connection =>
        {
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
INSERT INTO profile_cache (connection_ref, profile_identity, profile_version, payload, fetched_at)
VALUES ($ref, $id, $ver, $pl, $now)
ON CONFLICT(connection_ref) DO UPDATE SET
    profile_identity = excluded.profile_identity,
    profile_version = excluded.profile_version,
    payload = excluded.payload,
    fetched_at = excluded.fetched_at;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$id", profileIdentity);
            cmd.Parameters.AddWithValue("$ver", profileVersion);
            cmd.Parameters.AddWithValue("$pl", payload);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return Result.Ok();
        }, ct);

    private static SystemClaimRow ReadClaimRow(SqliteDataReader reader) =>
        new(
            ClaimId: reader.GetString(0),
            ConnectionRef: reader.GetString(1),
            WorktreeFingerprint: reader.GetString(2),
            WorkItemId: reader.GetInt32(3),
            State: reader.GetString(4),
            CasToken: reader.GetString(5),
            MintedAt: DateTimeOffset.Parse(reader.GetString(6)).ToUniversalTime(),
            EndedAt: reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)).ToUniversalTime(),
            RecordJson: reader.GetString(8));

    private async Task<Result<T>> ExecuteAsync<T>(Func<SqliteConnection, Task<Result<T>>> body, CancellationToken ct)
    {
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return Result.Fail<T>(openFailure!);
            return await body(connection!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return Result.Fail<T>(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return Result.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
    }

    private async Task<Result> ExecuteWriteAsync(Func<SqliteConnection, Task<Result>> body, CancellationToken ct)
    {
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return Result.Fail(openFailure!);
            using var tx = connection!.BeginTransaction(deferred: false);
            var result = await body(connection).ConfigureAwait(false);
            if (result.IsSuccess) tx.Commit(); else tx.Rollback();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return Result.Fail(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return Result.Fail($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
    }

    private bool TryEnsureOpen(out SqliteConnection? connection, out string? failure)
    {
        lock (_connectionGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SqliteSystemWorktreeRegistry));
            if (_openFailure is not null) { connection = null; failure = _openFailure; return false; }
            if (_connection is { State: System.Data.ConnectionState.Open }) { connection = _connection; failure = null; return true; }

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Snapshot file existence BEFORE opening — SqliteOpenMode.ReadWriteCreate
            // will create the file, so this is our one and only signal that
            // this is a fresh initialization vs an existing store we must
            // validate before touching.
            var isNewDb = !File.Exists(_dbPath);

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5,
            }.ToString();
            var newConnection = new SqliteConnection(cs);
            newConnection.Open();

            using (var pragma = newConnection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            if (isNewDb)
            {
                EnsureSchema(newConnection);
            }
            else
            {
                // Existing DB — validate BEFORE running any DDL that would
                // mutate layout_meta. On mismatch, the connection is disposed
                // and the failure is sticky for the process lifetime.
                if (!ValidateExistingSchema(newConnection))
                {
                    _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                    newConnection.Dispose();
                    connection = null; failure = _openFailure; return false;
                }
            }

            _connection = newConnection;
            connection = _connection; failure = null; return true;
        }
    }

    private static bool ValidateExistingSchema(SqliteConnection connection)
    {
        // Check layout_meta table exists.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='layout_meta' LIMIT 1;";
            var exists = cmd.ExecuteScalar();
            if (exists is null) return false;
        }
        // Exact-match the version.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM layout_meta WHERE id = 1;";
            var raw = cmd.ExecuteScalar();
            if (raw is null) return false;
            return Convert.ToInt32(raw) == SchemaVersion;
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction(deferred: false);
        using (var cmd = connection.CreateCommand())
        {
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
    work_item_id INTEGER NOT NULL,
    state TEXT NOT NULL,
    cas_token TEXT NOT NULL,
    minted_at TEXT NOT NULL,
    ended_at TEXT,
    record_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_claims_worktree_fingerprint ON claims(worktree_fingerprint);
CREATE INDEX IF NOT EXISTS idx_claims_connection_work_item ON claims(connection_ref, work_item_id);
CREATE INDEX IF NOT EXISTS idx_claims_state ON claims(state);
-- Partial unique index: enforces at most one pending or active claim per
-- (connection_ref, work_item_id) at the storage layer, matching the T1
-- v1 reserved-state kinds. Released/superseded/retired rows are excluded.
CREATE UNIQUE INDEX IF NOT EXISTS idx_claims_unique_reserved
    ON claims(connection_ref, work_item_id)
    WHERE state IN ('pending', 'active');
CREATE TABLE IF NOT EXISTS profile_cache (
    connection_ref TEXT PRIMARY KEY REFERENCES connections(connection_ref) ON DELETE RESTRICT,
    profile_identity TEXT NOT NULL,
    profile_version TEXT NOT NULL,
    payload TEXT NOT NULL,
    fetched_at TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = connection.CreateCommand())
        {
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
    }
}
