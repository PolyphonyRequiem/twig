using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

/// <summary>Immutable review context plus recoverable native progress. Completed rows remain history.</summary>
internal sealed record ConnectionBindingTransitionRecord(
    int Version, string Digest, string Fingerprint, string WorktreeRoot, string ConnectionRef, string State,
    string? OriginalBindingPin, string? DesiredBindingPin,
    ResolvedConnectionBinding OriginalBinding, ResolvedConnectionBinding DesiredBinding,
    string? DefaultBindingId, long? DefaultRevision,
    string ConfigurationHash, string WorktreeHash, string LayoutHash, string AttachmentHash,
    AttachmentDocument OriginalAttachment, AttachmentDocument DesiredAttachment,
    string Generation, string SourceMirror, string CurrentMirror,
    ConnectionMigrationRecord OriginalMigration, bool ResetRequired);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string BindingTransitionSchemaSql = """
        CREATE TABLE IF NOT EXISTS connection_binding_transitions (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            digest TEXT NOT NULL UNIQUE,
            worktree_fingerprint TEXT NOT NULL,
            generation TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('preparing','central-committed','attachment-completed','mirror-completed','completed')),
            record_json TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS binding_transition_unfinished
            ON connection_binding_transitions(worktree_fingerprint) WHERE state <> 'completed';
        """;

    internal Task<Result<ConnectionBindingTransitionRecord?>> ReadConnectionBindingTransitionAsync(
        string fingerprint, CancellationToken ct = default)
        => ExecuteReadAsync<ConnectionBindingTransitionRecord?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT digest,state,generation,record_json FROM connection_binding_transitions WHERE worktree_fingerprint=$fp ORDER BY (state <> 'completed') DESC,sequence DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Ok<ConnectionBindingTransitionRecord?>(null);
            var record = JsonSerializer.Deserialize(reader.GetString(3), TwigJsonContext.Default.ConnectionBindingTransitionRecord);
            if (record is null || record.Version != 1 || record.Digest != reader.GetString(0)
                || record.State != reader.GetString(1) || record.Generation != reader.GetString(2) || record.Fingerprint != fingerprint)
                return Result.Fail<ConnectionBindingTransitionRecord?>("binding-transition-inconsistent: native intent admission columns disagree with its record; preserve it for recovery.");
            return Result.Ok<ConnectionBindingTransitionRecord?>(record);
        }, ct);

    internal Task<Result> BeginConnectionBindingTransitionAsync(ConnectionBindingTransitionRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var admission = await ValidateTransitionAuthorityAsync(connection, tx, record, record.OriginalMigration, ct).ConfigureAwait(false);
            if (!admission.IsSuccess) return admission;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO connection_binding_transitions(digest,worktree_fingerprint,generation,state,record_json) VALUES($digest,$fp,$generation,'preparing',$json) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("$digest", record.Digest);
            cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
            cmd.Parameters.AddWithValue("$generation", record.Generation);
            cmd.Parameters.AddWithValue("$json", SerializeTransition(record));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Ok()
                : Result.Fail("binding-transition-conflict: resume the original native intent and digest; no new intent was admitted.");
        }, ct);

    /// <summary>Selection and generation commit together; local completion is still fenced by the unfinished row.</summary>
    internal Task<Result> CommitConnectionBindingTransitionAsync(ConnectionBindingTransitionRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            if (record.State != "preparing") return Result.Fail("binding-transition-state-mismatch");
            var admission = await ValidateTransitionAuthorityAsync(connection, tx, record, record.OriginalMigration, ct).ConfigureAwait(false);
            if (!admission.IsSuccess) return admission;
            var migration = TransitionMigration(record);
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE connection_migrations SET generation=$generation,record_json=$json WHERE worktree_fingerprint=$fp AND state='active' AND generation=$old AND record_json=$original;";
                cmd.Parameters.AddWithValue("$generation", migration.Generation);
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(migration, TwigJsonContext.Default.ConnectionMigrationRecord));
                cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
                cmd.Parameters.AddWithValue("$old", record.OriginalMigration.Generation);
                cmd.Parameters.AddWithValue("$original", JsonSerializer.Serialize(record.OriginalMigration, TwigJsonContext.Default.ConnectionMigrationRecord));
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    return Result.Fail("binding-transition-generation-cas-mismatch: original admission changed; retain and recover the original intent.");
            }
            return await AdvanceTransitionAsync(connection, tx, record, "central-committed", ct).ConfigureAwait(false);
        }, ct);

    internal Task<Result> AdvanceConnectionBindingTransitionAsync(ConnectionBindingTransitionRecord record, string state, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var expected = record.State switch
            {
                "central-committed" => "attachment-completed",
                "attachment-completed" => "mirror-completed",
                "mirror-completed" => "completed",
                _ => null
            };
            if (expected != state) return Result.Fail("binding-transition-state-mismatch: native recovery progress must advance in order.");
            var admission = await ValidateTransitionAuthorityAsync(connection, tx, record, TransitionMigration(record), ct).ConfigureAwait(false);
            if (!admission.IsSuccess) return admission;
            return await AdvanceTransitionAsync(connection, tx, record, state, ct).ConfigureAwait(false);
        }, ct);

    internal static ConnectionMigrationRecord TransitionMigration(ConnectionBindingTransitionRecord record)
        => record.OriginalMigration with
        {
            Generation = record.Generation,
            BindingId = record.DesiredBinding.Binding.BindingId,
            IdentityName = record.DesiredBinding.Identity.Name,
            CurrentMirror = record.CurrentMirror,
            AttachmentRevision = record.DesiredAttachment.Revision
        };

    private static async Task<Result> AdvanceTransitionAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionBindingTransitionRecord record, string state, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE connection_binding_transitions SET state=$state,record_json=$json WHERE digest=$digest AND worktree_fingerprint=$fp AND state=$expected AND record_json=$original;";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$json", SerializeTransition(record with { State = state }));
        cmd.Parameters.AddWithValue("$digest", record.Digest);
        cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
        cmd.Parameters.AddWithValue("$expected", record.State);
        cmd.Parameters.AddWithValue("$original", SerializeTransition(record));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Ok()
            : Result.Fail("binding-transition-progress-cas-mismatch: resume the exact native progress; no second transition was admitted.");
    }

    private static async Task<Result> ValidateTransitionAuthorityAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionBindingTransitionRecord record, ConnectionMigrationRecord expectedMigration, CancellationToken ct, string? defaultDigest = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM connection_default_transitions WHERE connection_ref=$ref AND state<>'completed' AND digest<>$family LIMIT 1;";
        cmd.Parameters.AddWithValue("$ref", record.ConnectionRef);
        cmd.Parameters.AddWithValue("$family", defaultDigest ?? "");
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            return Result.Fail("binding-default-family-incomplete: resume the exact native default family before changing a local selection.");
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT 1 FROM worktrees WHERE worktree_fingerprint=$fp AND connection_ref=$ref AND worktree_root=$root AND retired_at IS NULL;";
        cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
        cmd.Parameters.AddWithValue("$ref", record.ConnectionRef);
        cmd.Parameters.AddWithValue("$root", record.WorktreeRoot);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            return Result.Fail("binding-transition-worktree-cas-mismatch: registered checkout changed or retired.");
        cmd.CommandText = "SELECT 1 FROM connection_remote_write_intents i WHERE i.worktree_fingerprint=$fp AND NOT EXISTS(SELECT 1 FROM connection_remote_write_receipts r WHERE r.intent_id=i.intent_id) LIMIT 1;";
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            return Result.Fail("binding-transition-remote-write-unknown: native remote mutation admission has no settled outcome. Process death/lease expiry is not reconciliation; inspect and prove the original actor's exact outcome separately.");
        cmd.CommandText = "SELECT 1 FROM claims WHERE worktree_fingerprint=$fp AND state NOT IN ($released,$superseded) LIMIT 1;";
        cmd.Parameters.AddWithValue("$released", Twig.Domain.Services.Claims.ClaimStates.Released);
        cmd.Parameters.AddWithValue("$superseded", Twig.Domain.Services.Claims.ClaimStates.Superseded);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            return Result.Fail("binding-transition-claim-active: settle reserved/active/unknown claims through their ordinary lifecycle; no holder is rewritten.");
        cmd.CommandText = "SELECT binding_id,revision FROM connection_defaults WHERE connection_ref=$ref;";
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var exists = await reader.ReadAsync(ct).ConfigureAwait(false);
            if (exists != (record.DefaultBindingId is not null) || exists
                && (reader.GetString(0) != record.DefaultBindingId || reader.GetInt64(1) != record.DefaultRevision))
                return Result.Fail("binding-transition-default-cas-mismatch: default selection changed since the exact preview.");
        }
        foreach (var binding in new[] { record.OriginalBinding.Binding, record.DesiredBinding.Binding })
        {
            cmd.CommandText = "SELECT 1 FROM connection_bindings WHERE binding_id=$binding AND connection_ref=$ref AND identity_id=$identity AND revision=$revision;";
            cmd.Parameters.AddWithValue("$binding", binding.BindingId);
            cmd.Parameters.AddWithValue("$identity", binding.IdentityId);
            cmd.Parameters.AddWithValue("$revision", binding.Revision);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return Result.Fail("binding-transition-binding-cas-mismatch: reviewed binding authority changed or disappeared.");
            cmd.Parameters.RemoveAt("$binding");
            cmd.Parameters.RemoveAt("$identity");
            cmd.Parameters.RemoveAt("$revision");
        }
        cmd.CommandText = "SELECT record_json FROM connection_migrations WHERE worktree_fingerprint=$fp AND state=$migrationState AND generation=$generation;";
        cmd.Parameters.AddWithValue("$generation", expectedMigration.Generation);
        cmd.Parameters.AddWithValue("$migrationState", expectedMigration.State);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json == JsonSerializer.Serialize(expectedMigration, TwigJsonContext.Default.ConnectionMigrationRecord) ? Result.Ok()
            : Result.Fail("binding-transition-generation-cas-mismatch: original native generation is not the admitted authority.");
    }

    private static string SerializeTransition(ConnectionBindingTransitionRecord record)
        => JsonSerializer.Serialize(record, TwigJsonContext.Default.ConnectionBindingTransitionRecord);
}
