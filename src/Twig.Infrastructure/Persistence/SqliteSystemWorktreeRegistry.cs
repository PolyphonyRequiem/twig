using System.Collections.Concurrent;
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
/// Schema is at <see cref="SchemaVersion"/> 2 (AB#739 tuple storage bump —
/// the <c>primary_scope_kind</c> column and the extended partial-unique
/// index it participates in). A file whose <c>layout_meta.version</c>
/// disagrees with <see cref="SchemaVersion"/> fails closed with
/// <c>system-store-schema-mismatch</c>; no silent migration adopts an
/// older shape and then trips on the missing column. AB#739's tuple
/// storage bump is a hard bump: the T2 §Schema clause names it.
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
internal sealed class SqliteSystemWorktreeRegistry : ISystemWorktreeRegistry, IDisposable
{
    // AB#739 bump from 1 → 2 for the tuple-storage schema change
    // (`primary_scope_kind` column + extended partial unique index).
    private const int SchemaVersion = 3;
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
                return Result.Ok<SystemWorktreeRow?>(null);
            var connectionRef = reader.GetString(0);
            DateTimeOffset? retiredAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1)).ToUniversalTime();
            return Result.Ok<SystemWorktreeRow?>(new SystemWorktreeRow(connectionRef, retiredAt));
        }, ct);

    public Task<Result> UpsertConnectionAsync(string connectionRef, string organization, string project, string? team, CancellationToken ct = default)
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
            return Result.Ok();
        }, ct);

    public Task<Result> UpsertWorktreeAsync(string worktreeFingerprint, string connectionRef, string worktreeRoot, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
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
            return Result.Ok();
        }, ct);

    public Task<Result> InsertClaimAsync(
        string claimId, string connectionRef, string worktreeFingerprint,
        string primaryScopeKind, int workItemId, string state, string casToken, string recordJson, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using (var check = connection.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "SELECT connection_ref, retired_at FROM worktrees WHERE worktree_fingerprint = $fp LIMIT 1;";
                check.Parameters.AddWithValue("$fp", worktreeFingerprint);
                await using var reader = await check.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    return Result.Fail(AttachmentStorageFailure.WorktreeNotRegistered);
                var storedConnectionRef = reader.GetString(0);
                var retiredAtIsNull = reader.IsDBNull(1);
                // Byte-match: the claim's connectionRef must equal the
                // worktree row's stored connectionRef. Otherwise a caller
                // holding a fingerprint registered to connection A could
                // insert a claim tagged with connection B and cross the
                // binding — a corruption path the fingerprint-only precheck
                // silently allowed.
                if (!string.Equals(storedConnectionRef, connectionRef, StringComparison.Ordinal))
                    return Result.Fail(AttachmentStorageFailure.AttachmentConnectionMismatch);
                if (!retiredAtIsNull)
                    return Result.Fail(AttachmentStorageFailure.WorktreeRetired);
            }

            var now = _clock.GetUtcNow().ToString("o");
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO claims (claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json)
VALUES ($id, $ref, $fp, $kind, $wi, $state, $tok, $now, NULL, $json);";
                cmd.Parameters.AddWithValue("$id", claimId);
                cmd.Parameters.AddWithValue("$ref", connectionRef);
                cmd.Parameters.AddWithValue("$fp", worktreeFingerprint);
                cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
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
                return Result.Fail($"{AttachmentStorageFailure.ClaimDuplicateReserved}: {ex.Message}");
            }
        }, ct);

    public Task<Result> UpdateClaimStateAsync(
        string claimId, string expectedCasToken, string newCasToken,
        string state, DateTimeOffset? endedAt, string recordJson, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
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
        => ExecuteReadAsync<SystemClaimRow?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json FROM claims WHERE claim_id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", claimId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemClaimRow?>(null);
            return Result.Ok<SystemClaimRow?>(ReadClaimRow(reader));
        }, ct);

    public async Task<Result<SystemClaimRow?>> FindReservedClaimAsync(string connectionRef, string primaryScopeKind, int workItemId, IReadOnlyList<string> reservedStates, CancellationToken ct = default)
    {
        if (reservedStates.Count == 0)
            return Result.Ok<SystemClaimRow?>(null);
        return await ExecuteReadAsync<SystemClaimRow?>(async connection =>
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
SELECT claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json
  FROM claims
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi
   AND state IN ({string.Join(", ", placeholders)})
 LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok<SystemClaimRow?>(null);
            return Result.Ok<SystemClaimRow?>(ReadClaimRow(reader));
        }, ct).ConfigureAwait(false);
    }

    public Task<Result<IReadOnlyList<SystemClaimRow>>> FindClaimsForTupleAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default)
        => ExecuteReadAsync<IReadOnlyList<SystemClaimRow>>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json
  FROM claims
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            var rows = new List<SystemClaimRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add(ReadClaimRow(reader));
            return Result.Ok<IReadOnlyList<SystemClaimRow>>(rows);
        }, ct);

    public Task<Result> SupersedeAndActivateClaimAsync(
        string newClaimId,
        string newCasToken,
        string connectionRef,
        string worktreeFingerprint,
        string primaryScopeKind,
        int workItemId,
        string newRecordJson,
        string predecessorClaimId,
        string predecessorExpectedCasToken,
        string predecessorNewCasToken,
        string predecessorRecordJson,
        DateTimeOffset transitionAt,
        CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var stamp = transitionAt.ToUniversalTime().ToString("o");
            using (var supersede = connection.CreateCommand())
            {
                supersede.Transaction = tx;
                supersede.CommandText = @"
UPDATE claims
   SET state = 'superseded',
       cas_token = $newTok,
       ended_at = $endedAt,
       record_json = $json
 WHERE claim_id = $id AND cas_token = $expTok;";
                supersede.Parameters.AddWithValue("$id", predecessorClaimId);
                supersede.Parameters.AddWithValue("$expTok", predecessorExpectedCasToken);
                supersede.Parameters.AddWithValue("$newTok", predecessorNewCasToken);
                supersede.Parameters.AddWithValue("$endedAt", stamp);
                supersede.Parameters.AddWithValue("$json", predecessorRecordJson);
                var rows = await supersede.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
            }

            try
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO claims (claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json)
VALUES ($id, $ref, $fp, $kind, $wi, 'active', $tok, $now, NULL, $json);";
                insert.Parameters.AddWithValue("$id", newClaimId);
                insert.Parameters.AddWithValue("$ref", connectionRef);
                insert.Parameters.AddWithValue("$fp", worktreeFingerprint);
                insert.Parameters.AddWithValue("$kind", primaryScopeKind);
                insert.Parameters.AddWithValue("$wi", workItemId);
                insert.Parameters.AddWithValue("$tok", newCasToken);
                insert.Parameters.AddWithValue("$now", stamp);
                insert.Parameters.AddWithValue("$json", newRecordJson);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                return Result.Fail($"{AttachmentStorageFailure.ClaimDuplicateReserved}: {ex.Message}");
            }

            return Result.Ok();
        }, ct);
    public Task<Result<long>> ReserveTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default)
        => ExecuteWriteAsync<long>(async (connection, tx) =>
        {
            // Atomic increment: INSERT-OR-UPDATE + return the new value.
            // The BEGIN IMMEDIATE outer transaction serializes concurrent
            // reservers across processes at the SQLite level; the returned
            // epoch is strictly monotonic per tuple.
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT INTO tuple_epochs (connection_ref, primary_scope_kind, work_item_id, current_epoch, winning_claim_id, winning_cas_token)
VALUES ($ref, $kind, $wi, 1, NULL, NULL)
ON CONFLICT(connection_ref, primary_scope_kind, work_item_id) DO UPDATE SET
    current_epoch = tuple_epochs.current_epoch + 1
RETURNING current_epoch;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            var raw = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (raw is null || raw is DBNull)
                return Result.Fail<long>(AttachmentStorageFailure.SystemStoreSchemaMismatch);
            return Result.Ok(Convert.ToInt64(raw));
        }, ct);

    public Task<Result> CommitTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, long expectedEpoch, string winningClaimId, string winningCasToken, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
UPDATE tuple_epochs
   SET winning_claim_id = $cid,
       winning_cas_token = $cas
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi
   AND current_epoch = $epoch;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            cmd.Parameters.AddWithValue("$epoch", expectedEpoch);
            cmd.Parameters.AddWithValue("$cid", winningClaimId);
            cmd.Parameters.AddWithValue("$cas", winningCasToken);
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                return Result.Fail(AttachmentStorageFailure.ClaimTupleEpochMismatch);
            return Result.Ok();
        }, ct);

    public Task<Result<TupleEpochRow>> GetTupleEpochAsync(string connectionRef, string primaryScopeKind, int workItemId, CancellationToken ct = default)
        => ExecuteReadAsync<TupleEpochRow>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SELECT current_epoch, winning_claim_id, winning_cas_token
  FROM tuple_epochs
 WHERE connection_ref = $ref AND primary_scope_kind = $kind AND work_item_id = $wi LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$kind", primaryScopeKind);
            cmd.Parameters.AddWithValue("$wi", workItemId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result.Ok(new TupleEpochRow(0, null, null));
            var epoch = reader.GetInt64(0);
            var claimId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var cas = reader.IsDBNull(2) ? null : reader.GetString(2);
            return Result.Ok(new TupleEpochRow(epoch, claimId, cas));
        }, ct);


    public Task<Result> ActivateClaimAndCommitEpochAsync(
        string claimId, string expectedCasToken, string newCasToken,
        DateTimeOffset activatedAt, string recordJson,
        string connectionRef, string primaryScopeKind, int workItemId,
        long expectedEpoch, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var stamp = activatedAt.ToUniversalTime().ToString("o");
            using (var upd = connection.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = @"
UPDATE claims
   SET state = 'active',
       cas_token = $newTok,
       ended_at = NULL,
       record_json = $json
 WHERE claim_id = $id AND cas_token = $expTok;";
                upd.Parameters.AddWithValue("$id", claimId);
                upd.Parameters.AddWithValue("$expTok", expectedCasToken);
                upd.Parameters.AddWithValue("$newTok", newCasToken);
                upd.Parameters.AddWithValue("$json", recordJson);
                var rows = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
            }
            using (var epoch = connection.CreateCommand())
            {
                epoch.Transaction = tx;
                epoch.CommandText = @"
UPDATE tuple_epochs
   SET winning_claim_id = $cid,
       winning_cas_token = $cas
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi
   AND current_epoch = $epoch;";
                epoch.Parameters.AddWithValue("$ref", connectionRef);
                epoch.Parameters.AddWithValue("$kind", primaryScopeKind);
                epoch.Parameters.AddWithValue("$wi", workItemId);
                epoch.Parameters.AddWithValue("$epoch", expectedEpoch);
                epoch.Parameters.AddWithValue("$cid", claimId);
                epoch.Parameters.AddWithValue("$cas", newCasToken);
                var rows = await epoch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimTupleEpochMismatch);
            }
            _ = stamp;
            return Result.Ok();
        }, ct);

    public Task<Result> SupersedeAndActivateClaimAndCommitEpochAsync(
        string newClaimId, string newCasToken,
        string connectionRef, string worktreeFingerprint,
        string primaryScopeKind, int workItemId, string newRecordJson,
        string predecessorClaimId, string predecessorExpectedCasToken,
        string predecessorNewCasToken, string predecessorRecordJson,
        DateTimeOffset transitionAt, long expectedEpoch, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var stamp = transitionAt.ToUniversalTime().ToString("o");
            using (var supersede = connection.CreateCommand())
            {
                supersede.Transaction = tx;
                supersede.CommandText = @"
UPDATE claims
   SET state = 'superseded',
       cas_token = $newTok,
       ended_at = $endedAt,
       record_json = $json
 WHERE claim_id = $id AND cas_token = $expTok;";
                supersede.Parameters.AddWithValue("$id", predecessorClaimId);
                supersede.Parameters.AddWithValue("$expTok", predecessorExpectedCasToken);
                supersede.Parameters.AddWithValue("$newTok", predecessorNewCasToken);
                supersede.Parameters.AddWithValue("$endedAt", stamp);
                supersede.Parameters.AddWithValue("$json", predecessorRecordJson);
                var rows = await supersede.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
            }
            try
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = @"
INSERT INTO claims (claim_id, connection_ref, worktree_fingerprint, primary_scope_kind, work_item_id, state, cas_token, minted_at, ended_at, record_json)
VALUES ($id, $ref, $fp, $kind, $wi, 'active', $tok, $now, NULL, $json);";
                insert.Parameters.AddWithValue("$id", newClaimId);
                insert.Parameters.AddWithValue("$ref", connectionRef);
                insert.Parameters.AddWithValue("$fp", worktreeFingerprint);
                insert.Parameters.AddWithValue("$kind", primaryScopeKind);
                insert.Parameters.AddWithValue("$wi", workItemId);
                insert.Parameters.AddWithValue("$tok", newCasToken);
                insert.Parameters.AddWithValue("$now", stamp);
                insert.Parameters.AddWithValue("$json", newRecordJson);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */)
            {
                return Result.Fail($"{AttachmentStorageFailure.ClaimDuplicateReserved}: {ex.Message}");
            }
            using (var epoch = connection.CreateCommand())
            {
                epoch.Transaction = tx;
                epoch.CommandText = @"
UPDATE tuple_epochs
   SET winning_claim_id = $cid,
       winning_cas_token = $cas
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi
   AND current_epoch = $epoch;";
                epoch.Parameters.AddWithValue("$ref", connectionRef);
                epoch.Parameters.AddWithValue("$kind", primaryScopeKind);
                epoch.Parameters.AddWithValue("$wi", workItemId);
                epoch.Parameters.AddWithValue("$epoch", expectedEpoch);
                epoch.Parameters.AddWithValue("$cid", newClaimId);
                epoch.Parameters.AddWithValue("$cas", newCasToken);
                var rows = await epoch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimTupleEpochMismatch);
            }
            return Result.Ok();
        }, ct);

    public Task<Result> TerminalizeClaimAndCommitEpochAsync(
        string claimId, string expectedCasToken, string newCasToken,
        DateTimeOffset endedAt, string recordJson,
        string connectionRef, string primaryScopeKind, int workItemId,
        long expectedEpoch, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var stamp = endedAt.ToUniversalTime().ToString("o");
            using (var upd = connection.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = @"
UPDATE claims
   SET state = 'released',
       cas_token = $newTok,
       ended_at = $endedAt,
       record_json = $json
 WHERE claim_id = $id AND cas_token = $expTok;";
                upd.Parameters.AddWithValue("$id", claimId);
                upd.Parameters.AddWithValue("$expTok", expectedCasToken);
                upd.Parameters.AddWithValue("$newTok", newCasToken);
                upd.Parameters.AddWithValue("$endedAt", stamp);
                upd.Parameters.AddWithValue("$json", recordJson);
                var rows = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimCasMismatch);
            }
            using (var epoch = connection.CreateCommand())
            {
                epoch.Transaction = tx;
                // Release: no winning claim id (tuple is released).
                epoch.CommandText = @"
UPDATE tuple_epochs
   SET winning_claim_id = NULL,
       winning_cas_token = NULL
 WHERE connection_ref = $ref
   AND primary_scope_kind = $kind
   AND work_item_id = $wi
   AND current_epoch = $epoch;";
                epoch.Parameters.AddWithValue("$ref", connectionRef);
                epoch.Parameters.AddWithValue("$kind", primaryScopeKind);
                epoch.Parameters.AddWithValue("$wi", workItemId);
                epoch.Parameters.AddWithValue("$epoch", expectedEpoch);
                var rows = await epoch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rows == 0)
                    return Result.Fail(AttachmentStorageFailure.ClaimTupleEpochMismatch);
            }
            return Result.Ok();
        }, ct);

    public Task<Result<SystemProfileCacheRow?>> ReadProfileCacheAsync(string connectionRef, CancellationToken ct = default)
        => ExecuteReadAsync<SystemProfileCacheRow?>(async connection =>
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
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var now = _clock.GetUtcNow().ToString("o");
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
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
            PrimaryScopeKind: reader.GetString(3),
            WorkItemId: reader.GetInt32(4),
            State: reader.GetString(5),
            CasToken: reader.GetString(6),
            MintedAt: DateTimeOffset.Parse(reader.GetString(7)).ToUniversalTime(),
            EndedAt: reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)).ToUniversalTime(),
            RecordJson: reader.GetString(9));

    private async Task<Result<T>> ExecuteReadAsync<T>(Func<SqliteConnection, Task<Result<T>>> body, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return Result.Fail<T>(openFailure!);
            return await body(connection!).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return Result.Fail<T>(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return Result.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<Result> ExecuteWriteAsync(Func<SqliteConnection, SqliteTransaction, Task<Result>> body, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryEnsureOpen(out var connection, out var openFailure))
                return Result.Fail(openFailure!);
            using var tx = connection!.BeginTransaction(deferred: false);
            var result = await body(connection, tx).ConfigureAwait(false);
            if (result.IsSuccess) tx.Commit(); else tx.Rollback();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return Result.Fail(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return Result.Fail($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
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
                return Result.Fail<T>(openFailure!);
            using var tx = connection!.BeginTransaction(deferred: false);
            var result = await body(connection, tx).ConfigureAwait(false);
            if (result.IsSuccess) tx.Commit(); else tx.Rollback();
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) { return Result.Fail<T>(AttachmentStorageFailure.SystemStoreLocked); }
        catch (SqliteException ex) { return Result.Fail<T>($"{AttachmentStorageFailure.SystemStoreSchemaMismatch}: {ex.Message}"); }
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
                    // Wait for a committed layout_meta then compare version.
                    if (!WaitForCommittedSchema(newConnection))
                    {
                        _openFailure = AttachmentStorageFailure.SystemStoreSchemaMismatch;
                        newConnection.Dispose();
                        connection = null; failure = _openFailure; return false;
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

    private static bool WaitForCommittedSchema(SqliteConnection connection)
    {
        for (var attempt = 0; attempt < OpenValidationRetryCount; attempt++)
        {
            var (present, version) = ProbeLayoutMeta(connection);
            if (present)
                return version == SchemaVersion;
            Thread.Sleep(OpenValidationRetryDelayMs);
        }
        return false;
    }

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
