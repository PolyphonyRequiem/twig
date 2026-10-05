using System.Text.Json;
using Twig.Domain.Common;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record ConnectionMigrationRecord(
    int Version, string Fingerprint, string WorktreeRoot, string ConnectionRef,
    string Generation, string State, string IdentityName, string BindingId,
    string SourceMirror, string SourceDurable, string CurrentMirror,
    string ConfigurationHash, string AttachmentHash, long AttachmentRevision);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string MigrationSchemaSql = """
        CREATE TABLE IF NOT EXISTS connection_migrations (
            worktree_fingerprint TEXT PRIMARY KEY,
            generation TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK (state IN ('preparing', 'active')),
            record_json TEXT NOT NULL
        );
        """;

    internal Task<Result<ConnectionMigrationRecord?>> ReadConnectionMigrationAsync(string fingerprint, CancellationToken ct = default)
        => ExecuteReadAsync<ConnectionMigrationRecord?>(async connection =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT state, generation, record_json FROM connection_migrations WHERE worktree_fingerprint = $fp;";
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Ok<ConnectionMigrationRecord?>(null);
            var record = JsonSerializer.Deserialize(reader.GetString(2), TwigJsonContext.Default.ConnectionMigrationRecord);
            if (record is null || record.State != reader.GetString(0) || record.Generation != reader.GetString(1))
                return Result.Fail<ConnectionMigrationRecord?>("migration-intent-inconsistent: native record disagrees with its admission columns.");
            return Result.Ok<ConnectionMigrationRecord?>(record);
        }, ct);

    internal Task<Result> BeginConnectionMigrationAsync(ConnectionMigrationRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var remote = await RefuseUnsettledMigrationWritesAsync(connection, tx, record.Fingerprint, ct).ConfigureAwait(false);
            if (!remote.IsSuccess) return remote;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO connection_migrations(worktree_fingerprint, generation, state, record_json) VALUES ($fp, $generation, 'preparing', $json) ON CONFLICT(worktree_fingerprint) DO NOTHING;";
            cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
            cmd.Parameters.AddWithValue("$generation", record.Generation);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, TwigJsonContext.Default.ConnectionMigrationRecord));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1
                ? Result.Ok() : Result.Fail("migration-intent-conflict: resume the existing native intent; no new generation was admitted.");
        }, ct);

    internal Task<Result> CompleteConnectionMigrationAsync(ConnectionMigrationRecord record, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            using var cmd = connection.CreateCommand();
            var remote = await RefuseUnsettledMigrationWritesAsync(connection, tx, record.Fingerprint, ct).ConfigureAwait(false);
            if (!remote.IsSuccess) return remote;
            using (var selection = connection.CreateCommand())
            {
                selection.Transaction = tx;
                selection.CommandText = "SELECT binding_id FROM connection_defaults WHERE connection_ref=$ref;";
                selection.Parameters.AddWithValue("$ref", record.ConnectionRef);
                if (!string.Equals(await selection.ExecuteScalarAsync(ct).ConfigureAwait(false) as string, record.BindingId, StringComparison.Ordinal))
                    return Result.Fail("migration-selection-cas-mismatch: default changed during preparation. Original intent stays fenced; restore its original selection before resuming.");
            }
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE connection_migrations SET state = 'active', record_json = $json WHERE worktree_fingerprint = $fp AND generation = $generation AND state = 'preparing';";
            cmd.Parameters.AddWithValue("$fp", record.Fingerprint);
            cmd.Parameters.AddWithValue("$generation", record.Generation);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record with { State = "active" }, TwigJsonContext.Default.ConnectionMigrationRecord));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1
                ? Result.Ok() : Result.Fail("migration-intent-conflict: activation CAS refused; retain the original intent and resume.");
        }, ct);

    private static async Task<Result> RefuseUnsettledMigrationWritesAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction tx, string fingerprint, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM connection_default_transition_members m JOIN connection_default_transitions f ON f.digest=m.digest WHERE m.worktree_fingerprint=$fp AND f.state<>'completed' LIMIT 1;";
        cmd.Parameters.AddWithValue("$fp", fingerprint);
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
            return Result.Fail("binding-default-family-incomplete: migration cannot replace or activate a member of an unfinished native default intent.");
        cmd.CommandText = "SELECT 1 FROM connection_remote_write_intents i WHERE i.worktree_fingerprint=$fp AND NOT EXISTS(SELECT 1 FROM connection_remote_write_receipts r WHERE r.intent_id=i.intent_id) LIMIT 1;";
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null ? Result.Ok()
            : Result.Fail("migration-remote-write-outcome-unknown: native mutation uncertainty must be reconciled under original authority; lease expiry cannot admit migration.");
    }
}
