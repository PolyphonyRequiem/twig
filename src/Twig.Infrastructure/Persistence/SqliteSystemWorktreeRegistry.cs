using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// SQLite-backed implementation of <see cref="ISystemWorktreeRegistry"/>.
/// Stores the AB#736 §4.3 <c>system.db</c> at <c>~/.twig/system.db</c> —
/// per-user, per-machine, never inside any Git tree — and exposes the exact
/// §9.4 registry surface AB#738 needs: connection + worktree upsert and a
/// non-retired matching lookup. AB#739's claim tables will live in the same
/// DB behind a schema migration.
/// <para>
/// The connection is opened lazily on the first call and reused across the
/// process. WAL mode is enabled on first open with <c>busy_timeout = 5000</c>
/// per §4.3.1; every mutating verb runs inside <c>BEGIN IMMEDIATE</c> so a
/// concurrent writer either observes the commit or receives
/// <c>SQLITE_BUSY</c>. Busy-after-retry surfaces as <c>system-store-locked</c>.
/// </para>
/// </summary>
internal sealed class SqliteSystemWorktreeRegistry : ISystemWorktreeRegistry, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _dbPath;
    private readonly TimeProvider _clock;
    private readonly object _connectionGate = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteSystemWorktreeRegistry(string dbPath, TimeProvider clock)
    {
        _dbPath = dbPath;
        _clock = clock;
    }

    public async Task<Result<SystemWorktreeRow?>> FindWorktreeAsync(string worktreeFingerprint, CancellationToken ct = default)
    {
        return await ExecuteAsync(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT connection_ref, retired_at FROM worktrees WHERE worktree_fingerprint = $fp LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemWorktreeRow?>(null);
            var connectionRef = reader.GetString(0);
            DateTimeOffset? retiredAt = reader.IsDBNull(1)
                ? null
                : DateTimeOffset.Parse(reader.GetString(1)).ToUniversalTime();
            return Result.Ok<SystemWorktreeRow?>(new SystemWorktreeRow(connectionRef, retiredAt));
        }, ct).ConfigureAwait(false);
    }

    public async Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default)
    {
        return await ExecuteWriteAsync(async connection =>
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
        }, ct).ConfigureAwait(false);
    }

    public async Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
    {
        return await ExecuteWriteAsync(async connection =>
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
        }, ct).ConfigureAwait(false);
    }

    private async Task<Result<T>> ExecuteAsync<T>(Func<SqliteConnection, Task<Result<T>>> body, CancellationToken ct)
    {
        try
        {
            var connection = EnsureOpen();
            return await body(connection).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5 /* SQLITE_BUSY */)
        {
            return Result.Fail<T>(AttachmentStorageFailure.SystemStoreLocked);
        }
        catch (SqliteException ex)
        {
            return Result.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}");
        }
    }

    private async Task<Result> ExecuteWriteAsync(Func<SqliteConnection, Task<Result>> body, CancellationToken ct)
    {
        try
        {
            var connection = EnsureOpen();
            using var tx = connection.BeginTransaction(deferred: false);
            var result = await body(connection).ConfigureAwait(false);
            if (result.IsSuccess)
                tx.Commit();
            else
                tx.Rollback();
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
        {
            return Result.Fail(AttachmentStorageFailure.SystemStoreLocked);
        }
        catch (SqliteException ex)
        {
            return Result.Fail($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}");
        }
    }

    private SqliteConnection EnsureOpen()
    {
        lock (_connectionGate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SqliteSystemWorktreeRegistry));
            if (_connection is { State: System.Data.ConnectionState.Open })
                return _connection;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5,
            }.ToString();
            var connection = new SqliteConnection(cs);
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(connection);
            _connection = connection;
            return connection;
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
CREATE INDEX IF NOT EXISTS idx_worktrees_connection_ref ON worktrees(connection_ref);";
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
