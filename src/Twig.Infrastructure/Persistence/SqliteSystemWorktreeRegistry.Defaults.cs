using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Common;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record ConnectionDefaultWorktreeRow(string Fingerprint, string WorktreeRoot, string InitializedAt, string LastSeenAt);
internal sealed record ConnectionDefaultSelection(string ConnectionRef, string BindingId, long Revision);
internal sealed record ConnectionDefaultMemberRecord(ConnectionDefaultWorktreeRow Registration, AttachmentDocument Attachment,
    string ConfigurationHash, string WorktreeHash, string LayoutHash, string AttachmentHash,
    ConnectionBindingTransitionRecord? Transition);
internal sealed record ConnectionDefaultTransitionRecord(int Version, string Digest, string ConnectionRef, string State,
    ConnectionDefaultSelection OriginalDefault, ConnectionDefaultSelection DesiredDefault, IdentityBinding DesiredBinding,
    AuthenticationIdentity DesiredIdentity, IReadOnlyList<ConnectionDefaultMemberRecord> Members);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string DefaultTransitionSchemaSql = """
        CREATE TABLE IF NOT EXISTS connection_default_transitions (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            digest TEXT NOT NULL UNIQUE,
            connection_ref TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('preparing','central-committed','completed')),
            record_json TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS default_transition_unfinished
            ON connection_default_transitions(connection_ref) WHERE state <> 'completed';
        CREATE TABLE IF NOT EXISTS connection_default_transition_members (
            digest TEXT NOT NULL REFERENCES connection_default_transitions(digest),
            worktree_fingerprint TEXT NOT NULL,
            PRIMARY KEY(digest,worktree_fingerprint)
        );
        """;

    internal Task<Result<IReadOnlyList<ConnectionDefaultWorktreeRow>>> ReadDefaultWorktreesAsync(string connectionRef, CancellationToken ct = default)
        => ExecuteReadAsync<IReadOnlyList<ConnectionDefaultWorktreeRow>>(async connection =>
            Result.Ok<IReadOnlyList<ConnectionDefaultWorktreeRow>>(await ReadDefaultWorktreesAsync(connection, null, connectionRef, ct).ConfigureAwait(false)), ct);

    private static async Task<List<ConnectionDefaultWorktreeRow>> ReadDefaultWorktreesAsync(SqliteConnection connection,
        SqliteTransaction? tx, string connectionRef, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT worktree_fingerprint,worktree_root,initialized_at,last_seen_at FROM worktrees WHERE connection_ref=$ref AND retired_at IS NULL ORDER BY worktree_fingerprint COLLATE BINARY;";
        cmd.Parameters.AddWithValue("$ref", connectionRef);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ConnectionDefaultWorktreeRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    internal Task<Result<ConnectionDefaultTransitionRecord?>> ReadDefaultTransitionAsync(string connectionRef, string? digest = null, CancellationToken ct = default)
        => ExecuteReadAsync<ConnectionDefaultTransitionRecord?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT digest,state,record_json FROM connection_default_transitions WHERE connection_ref=$ref AND (state <> 'completed' OR digest=$digest) ORDER BY (state <> 'completed') DESC,sequence DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$ref", connectionRef);
            cmd.Parameters.AddWithValue("$digest", (object?)digest ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Ok<ConnectionDefaultTransitionRecord?>(null);
            var record = JsonSerializer.Deserialize(reader.GetString(2), TwigJsonContext.Default.ConnectionDefaultTransitionRecord);
            return record is not null && record.Version == 1 && record.Digest == reader.GetString(0)
                && record.State == reader.GetString(1) && record.ConnectionRef == connectionRef
                ? Result.Ok<ConnectionDefaultTransitionRecord?>(record)
                : Result.Fail<ConnectionDefaultTransitionRecord?>("binding-default-intent-inconsistent: restore the original native family record; no new intent is admitted.");
        }, ct);

    internal Task<Result> InspectDefaultTransitionAuthorityAsync(ConnectionDefaultTransitionRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync((connection, tx) => ValidateDefaultAuthorityAsync(connection, tx, record, ct), ct);

    internal Task<Result> BeginDefaultTransitionAsync(ConnectionDefaultTransitionRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var admitted = await ValidateDefaultAuthorityAsync(connection, tx, record, ct).ConfigureAwait(false);
            if (!admitted.IsSuccess) return admitted;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO connection_default_transitions(digest,connection_ref,state,record_json) VALUES($digest,$ref,'preparing',$json) ON CONFLICT DO NOTHING;";
            cmd.Parameters.AddWithValue("$digest", record.Digest);
            cmd.Parameters.AddWithValue("$ref", record.ConnectionRef);
            cmd.Parameters.AddWithValue("$json", SerializeDefault(record));
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return Result.Fail("binding-default-intent-conflict: recover the original default intent and exact digest.");
            foreach (var member in record.Members.Where(x => x.Transition is not null))
            {
                cmd.CommandText = "INSERT INTO connection_default_transition_members(digest,worktree_fingerprint) VALUES($digest,$fp);";
                cmd.Parameters.AddWithValue("$fp", member.Registration.Fingerprint);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                var local = member.Transition!;
                cmd.CommandText = "UPDATE connection_migrations SET state='preparing',record_json=$prepared WHERE worktree_fingerprint=$fp AND state='active' AND record_json=$original;";
                cmd.Parameters.AddWithValue("$prepared", JsonSerializer.Serialize(local.OriginalMigration with { State = "preparing" }, TwigJsonContext.Default.ConnectionMigrationRecord));
                cmd.Parameters.AddWithValue("$original", JsonSerializer.Serialize(local.OriginalMigration, TwigJsonContext.Default.ConnectionMigrationRecord));
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                    return Result.Fail("binding-default-preparation-cas-mismatch: no member preparation was committed.");
                cmd.Parameters.RemoveAt("$fp"); cmd.Parameters.RemoveAt("$prepared"); cmd.Parameters.RemoveAt("$original");
            }
            return Result.Ok();
        }, ct);

    internal Task<Result> CommitDefaultTransitionAsync(ConnectionDefaultTransitionRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            if (record.State != "preparing") return Result.Fail("binding-default-state-mismatch");
            var admitted = await ValidateDefaultAuthorityAsync(connection, tx, record, ct).ConfigureAwait(false);
            if (!admitted.IsSuccess) return admitted;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE connection_defaults SET binding_id=$desired,revision=$new,updated_at=$now WHERE connection_ref=$ref AND binding_id=$original AND revision=$old;";
            cmd.Parameters.AddWithValue("$desired", record.DesiredDefault.BindingId);
            cmd.Parameters.AddWithValue("$new", record.DesiredDefault.Revision);
            cmd.Parameters.AddWithValue("$ref", record.ConnectionRef);
            cmd.Parameters.AddWithValue("$original", record.OriginalDefault.BindingId);
            cmd.Parameters.AddWithValue("$old", record.OriginalDefault.Revision);
            cmd.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToString("o"));
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Result.Fail("binding-default-selection-cas-mismatch");
            foreach (var member in record.Members)
            {
                if (member.Transition is not { } local) continue;
                var migration = TransitionMigration(local);
                cmd.CommandText = "UPDATE connection_migrations SET state='active',generation=$generation,record_json=$json WHERE worktree_fingerprint=$fp AND state='preparing' AND generation=$prior AND record_json=$priorJson;";
                cmd.Parameters.AddWithValue("$generation", migration.Generation);
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(migration, TwigJsonContext.Default.ConnectionMigrationRecord));
                cmd.Parameters.AddWithValue("$fp", local.Fingerprint);
                cmd.Parameters.AddWithValue("$prior", local.OriginalMigration.Generation);
                cmd.Parameters.AddWithValue("$priorJson", JsonSerializer.Serialize(local.OriginalMigration with { State = "preparing" }, TwigJsonContext.Default.ConnectionMigrationRecord));
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Result.Fail("binding-default-generation-cas-mismatch: no member was admitted.");
                cmd.Parameters.RemoveAt("$generation"); cmd.Parameters.RemoveAt("$json"); cmd.Parameters.RemoveAt("$fp");
                cmd.Parameters.RemoveAt("$prior"); cmd.Parameters.RemoveAt("$priorJson");
            }
            var desired = record with { State = "central-committed", Members = record.Members.Select(x => x.Transition is { } local
                ? x with { Transition = local with { State = "central-committed" } } : x).ToArray() };
            return await WriteDefaultProgressAsync(connection, tx, record, desired, ct).ConfigureAwait(false);
        }, ct);

    internal Task<Result> AdvanceDefaultTransitionAsync(ConnectionDefaultTransitionRecord original, ConnectionDefaultTransitionRecord desired, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            if (original.State != "central-committed" || desired.Digest != original.Digest || desired.ConnectionRef != original.ConnectionRef
                || desired.State is not ("central-committed" or "completed") || original.Members.Count != desired.Members.Count)
                return Result.Fail("binding-default-progress-invalid");
            var changes = 0;
            for (var i = 0; i < original.Members.Count; i++)
            {
                var before = original.Members[i]; var after = desired.Members[i];
                if (before.Transition == after.Transition)
                {
                    if (before != after) return Result.Fail("binding-default-member-context-changed");
                    continue;
                }
                if (before.Transition is not { } local || after != (before with { Transition = after.Transition })
                    || after.Transition != (local with { State = after.Transition?.State ?? "" }))
                    return Result.Fail("binding-default-member-context-changed");
                var next = local.State switch { "central-committed" => "attachment-completed", "attachment-completed" => "mirror-completed", "mirror-completed" => "completed", _ => null };
                if (after.Transition!.State != next) return Result.Fail("binding-default-member-progress-invalid");
                changes++;
            }
            if (desired.State == "completed" ? changes != 0 || desired.Members.Any(x => x.Transition is { State: not "completed" }) : changes != 1)
                return Result.Fail("binding-default-family-incomplete: all members must complete before any new runtime is admitted.");
            var admitted = await ValidateDefaultAuthorityAsync(connection, tx, original, ct).ConfigureAwait(false);
            return admitted.IsSuccess ? await WriteDefaultProgressAsync(connection, tx, original, desired, ct).ConfigureAwait(false) : admitted;
        }, ct);

    private static async Task<Result> WriteDefaultProgressAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionDefaultTransitionRecord original, ConnectionDefaultTransitionRecord desired, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE connection_default_transitions SET state=$state,record_json=$json WHERE digest=$digest AND state=$expected AND record_json=$original;";
        cmd.Parameters.AddWithValue("$state", desired.State); cmd.Parameters.AddWithValue("$json", SerializeDefault(desired));
        cmd.Parameters.AddWithValue("$digest", original.Digest); cmd.Parameters.AddWithValue("$expected", original.State); cmd.Parameters.AddWithValue("$original", SerializeDefault(original));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Ok() : Result.Fail("binding-default-progress-cas-mismatch: recover the exact native family intent.");
    }

    private static async Task<Result> ValidateDefaultAuthorityAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionDefaultTransitionRecord record, CancellationToken ct)
    {
        var currentRows = await ReadDefaultWorktreesAsync(connection, tx, record.ConnectionRef, ct).ConfigureAwait(false);
        if (!currentRows.SequenceEqual(record.Members.Select(x => x.Registration)))
            return Result.Fail("binding-default-worktree-set-cas-mismatch: complete registered membership or registration revision changed.");
        using var cmd = connection.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM connection_default_transitions WHERE connection_ref=$ref AND state<>'completed' AND digest<>$digest LIMIT 1;";
        cmd.Parameters.AddWithValue("$ref", record.ConnectionRef); cmd.Parameters.AddWithValue("$digest", record.Digest);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return Result.Fail("binding-default-intent-conflict");
        cmd.CommandText = "SELECT record_json FROM connection_default_transitions WHERE digest=$digest AND state='preparing';";
        var preparationJson = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        var preparationRecorded = preparationJson is not null;
        if (preparationRecorded && preparationJson != SerializeDefault(record))
            return Result.Fail("binding-default-preparation-cas-mismatch: the exact recorded native family changed.");
        var expected = record.State == "preparing" ? record.OriginalDefault : record.DesiredDefault;
        cmd.CommandText = "SELECT 1 FROM connection_defaults WHERE connection_ref=$ref AND binding_id=$binding AND revision=$revision;";
        cmd.Parameters.AddWithValue("$binding", expected.BindingId); cmd.Parameters.AddWithValue("$revision", expected.Revision);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return Result.Fail("binding-default-selection-cas-mismatch");
        cmd.CommandText = "SELECT 1 FROM connection_bindings WHERE binding_id=$desired AND connection_ref=$ref AND identity_id=$identity AND revision=$bindingRevision;";
        cmd.Parameters.AddWithValue("$desired", record.DesiredBinding.BindingId); cmd.Parameters.AddWithValue("$identity", record.DesiredBinding.IdentityId); cmd.Parameters.AddWithValue("$bindingRevision", record.DesiredBinding.Revision);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return Result.Fail("binding-default-binding-cas-mismatch");
        foreach (var member in record.Members)
        {
            if (member.Transition is not { } local) continue;
            cmd.CommandText = "SELECT 1 FROM connection_binding_transitions WHERE worktree_fingerprint=$fp AND state<>'completed' LIMIT 1;";
            cmd.Parameters.AddWithValue("$fp", local.Fingerprint);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null) return Result.Fail("binding-default-local-intent-unfinished");
            cmd.Parameters.RemoveAt("$fp");
            var authority = local with { DefaultBindingId = expected.BindingId, DefaultRevision = expected.Revision };
            var expectedMigration = record.State == "preparing"
                ? preparationRecorded ? local.OriginalMigration with { State = "preparing" } : local.OriginalMigration
                : TransitionMigration(local);
            var result = await ValidateTransitionAuthorityAsync(connection, tx, authority,
                expectedMigration, ct, record.Digest).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
        }
        return Result.Ok();
    }

    private static string SerializeDefault(ConnectionDefaultTransitionRecord record)
        => JsonSerializer.Serialize(record, TwigJsonContext.Default.ConnectionDefaultTransitionRecord);
}
