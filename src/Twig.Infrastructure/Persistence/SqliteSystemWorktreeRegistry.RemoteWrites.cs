using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record ConnectionRemoteWriteIntent(int Version, string IntentId, string RequestDigest,
    string Fingerprint, ResolvedConnectionBinding Origin, ConnectionRemoteWriteRequest Request, DateTimeOffset AdmittedAt);
internal sealed record ConnectionRemoteWriteObservation(int Version, string ObservationId, string IntentId,
    string RequestDigest, ConnectionRemoteWriteResponse Response, DateTimeOffset ObservedAt);
internal sealed record ConnectionRemoteWriteReceipt(int Version, string ReceiptId, string IntentId,
    string RequestDigest, string Kind, string? ObservationId, string EvidenceJson,
    ResolvedConnectionBinding AuthorizingOrigin, string? AuthorizerIdentity, string? Rationale, DateTimeOffset RecordedAt);
internal sealed record ConnectionRemoteWriteHistory(ConnectionRemoteWriteIntent Intent,
    IReadOnlyList<ConnectionRemoteWriteObservation> Observations, ConnectionRemoteWriteReceipt? Receipt);
internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string RemoteWriteSchemaSql = """
        CREATE TABLE IF NOT EXISTS connection_remote_write_intents (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            intent_id TEXT NOT NULL UNIQUE,
            request_digest TEXT NOT NULL,
            worktree_fingerprint TEXT NOT NULL,
            intent_json TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS remote_write_origin ON connection_remote_write_intents(worktree_fingerprint,request_digest);
        CREATE TABLE IF NOT EXISTS connection_remote_write_observations (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            observation_id TEXT NOT NULL UNIQUE,
            intent_id TEXT NOT NULL REFERENCES connection_remote_write_intents(intent_id) ON DELETE RESTRICT,
            observation_json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS connection_remote_write_receipts (
            intent_id TEXT PRIMARY KEY REFERENCES connection_remote_write_intents(intent_id) ON DELETE RESTRICT,
            receipt_id TEXT NOT NULL UNIQUE,
            receipt_json TEXT NOT NULL
        );
        """;

    internal Task<Result<bool>> BeginConnectionRemoteWriteAsync(ConnectionRemoteWriteIntent intent, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            if (intent.Request.SeedCorrelation is not null)
                return NativeResults.Fail("remote-write-correlation-retired: seed requests cannot authorize a current native write.");
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$fp", intent.Fingerprint);
            cmd.Parameters.AddWithValue("$ref", intent.Origin.Binding.ConnectionRef);
            cmd.Parameters.AddWithValue("$binding", intent.Origin.Binding.BindingId);
            cmd.Parameters.AddWithValue("$identity", intent.Origin.Binding.IdentityId);
            cmd.Parameters.AddWithValue("$revision", intent.Origin.Binding.Revision);
            cmd.Parameters.AddWithValue("$digest", intent.RequestDigest);
            cmd.CommandText = "SELECT 1 FROM connection_default_transition_members m JOIN connection_default_transitions f ON f.digest=m.digest WHERE m.worktree_fingerprint=$fp AND f.state<>'completed' LIMIT 1;";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return NativeResults.Fail("remote-write-default-unfinished: the original default family fences every affected member until all complete.");
            cmd.CommandText = "SELECT 1 FROM worktrees WHERE worktree_fingerprint=$fp AND connection_ref=$ref AND retired_at IS NULL;";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return NativeResults.Fail("remote-write-origin-unavailable: original attached checkout is not registered or is retired.");
            cmd.CommandText = "SELECT 1 FROM connection_bindings WHERE binding_id=$binding AND connection_ref=$ref AND identity_id=$identity AND revision=$revision;";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return NativeResults.Fail("remote-write-binding-changed: reconnect through the original admitted authority before mutation.");
            if (intent.Origin.SelectionSource == "connection-default-binding")
            {
                cmd.CommandText = "SELECT 1 FROM connection_defaults WHERE connection_ref=$ref AND binding_id=$binding AND revision=$selection;";
                cmd.Parameters.AddWithValue("$selection", intent.Origin.SelectionRevision);
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                    return NativeResults.Fail("remote-write-selection-cas-mismatch: current default no longer matches admitted authority.");
            }
            if (intent.Origin.StorageGeneration is not { } generation
                || !generation.StartsWith("core:", StringComparison.Ordinal)
                || !Guid.TryParseExact(generation.AsSpan(5), "N", out var instance) || instance == Guid.Empty
                || intent.Request.CoreCorrelation is not { } correlation || correlation.StoreInstance == Guid.Empty
                || correlation.Attempt == Guid.Empty || correlation.Operation < 0)
                return NativeResults.Fail("remote-write-generation-cas-mismatch: the exact current Core generation and operation correlation are required.");
            var gate = await ReadCoreGate(connection, tx, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return NativeResults.Fail(gate.Error);
            var originSource = gate.Value.Sources.SingleOrDefault(source => source.Instance == instance);
            var operationSource = gate.Value.Sources.SingleOrDefault(source => source.Instance == correlation.StoreInstance);
            if (!gate.Value.Active || originSource is null || operationSource is null
                || !NativePathComparer.Instance.Equals(originSource.Root, intent.Origin.WorktreeRoot)
                || !NativePathComparer.Instance.Equals(operationSource.Root, intent.Origin.WorktreeRoot)
                || originSource.Connection != operationSource.Connection
                || operationSource.Connection.Provider.Value != "ado"
                || CleanCoreAdoAccess.ConnectionText(operationSource.Connection) != correlation.ConnectionIdentity)
                return NativeResults.Fail("remote-write-core-source-mismatch: the current origin and operation must retain the exact active Core source authority.");
            cmd.CommandText = """
                SELECT 1 FROM core_attempt_refs WHERE instance=$store AND digest=$proposal
                    AND operation=$operation AND attempt=$attempt AND ever_applying=1 LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$store", correlation.StoreInstance.ToString("D"));
            cmd.Parameters.AddWithValue("$proposal", correlation.ProposalDigest);
            cmd.Parameters.AddWithValue("$operation", correlation.Operation);
            cmd.Parameters.AddWithValue("$attempt", correlation.Attempt.ToString("D"));
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return NativeResults.Fail("remote-write-core-attempt-unavailable: the exact Applying attempt has no current native source reference.");
            cmd.CommandText = "SELECT 1 FROM connection_binding_transitions WHERE worktree_fingerprint=$fp AND state<>'completed' LIMIT 1;";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return NativeResults.Fail("remote-write-transition-unfinished: native transition fences mutation admission.");
            cmd.CommandText = "SELECT 1 FROM connection_activations WHERE worktree_fingerprint=$fp AND state='preparing';";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return NativeResults.Fail("remote-write-activation-unfinished: native activation fences mutation admission.");
            if (intent.Request.CoreCorrelation is { } core)
            {
                cmd.CommandText = """
                    SELECT i.intent_json,r.receipt_json FROM connection_remote_write_intents i
                    LEFT JOIN connection_remote_write_receipts r ON r.intent_id=i.intent_id
                    WHERE i.worktree_fingerprint=$fp;
                    """;
                using var prior = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await prior.ReadAsync(ct).ConfigureAwait(false))
                {
                    var admitted = JsonSerializer.Deserialize(prior.GetString(0), TwigJsonContext.Default.ConnectionRemoteWriteIntent);
                    if (admitted is null)
                        return NativeResults.Fail("remote-write-native-evidence-unreadable: original native evidence cannot authorize another request.");
                    if (admitted.Request.CoreCorrelation is { } original
                        && (original.Attempt == core.Attempt
                            || original.ProposalDigest == core.ProposalDigest && original.Operation == core.Operation
                            || core.CreationIdentity is not null && original.CreationIdentity == core.CreationIdentity))
                        return NativeResults.Fail("remote-write-core-attempt-fenced: this operation, attempt or creation UUID has already been admitted; no replacement request is authorized.");
                    if (!prior.IsDBNull(1)) continue;
                    if (core.CreationIdentity is not null && admitted.Request.EffectKind == "workitem-create"
                        || admitted.Request.CoreCorrelation?.TargetIdentity == core.TargetIdentity
                        || ConnectionRemoteWriteAdmission.TargetWorkItemId(intent.Request.Target) is { } target
                            && ConnectionRemoteWriteAdmission.TargetWorkItemId(admitted.Request.Target) == target)
                        return NativeResults.Fail("remote-write-core-outcome-unknown: an original unresolved native request fences this target or creation family.");
                }
            }
            cmd.CommandText = "SELECT 1 FROM connection_remote_write_intents i WHERE i.worktree_fingerprint=$fp AND i.request_digest=$digest AND NOT EXISTS(SELECT 1 FROM connection_remote_write_receipts r WHERE r.intent_id=i.intent_id) LIMIT 1;";
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return NativeResults.Fail("remote-write-outcome-unknown: the same semantic request already has an unsettled native intent. Inspect and reconcile its original evidence; no automatic retry/replay or pending discard settles it.");
            cmd.CommandText = "INSERT INTO connection_remote_write_intents(intent_id,request_digest,worktree_fingerprint,intent_json) VALUES($id,$digest,$fp,$json);";
            cmd.Parameters.AddWithValue("$id", intent.IntentId);
            cmd.Parameters.AddWithValue("$json", SerializeRemoteIntent(intent));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return NativeResults.Ok();
        }, ct);

    internal Task<Result<bool>> ObserveConnectionRemoteWriteAsync(ConnectionRemoteWriteIntent intent,
        ConnectionRemoteWriteObservation observation, ConnectionRemoteWriteReceipt? receipt, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var original = await ValidateRemoteIntentAsync(connection, tx, intent, ct).ConfigureAwait(false);
            if (!original.IsSuccess) return original;
            if (observation.IntentId != intent.IntentId || observation.RequestDigest != intent.RequestDigest)
                return NativeResults.Fail("remote-write-observation-origin-mismatch");
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO connection_remote_write_observations(observation_id,intent_id,observation_json) VALUES($observation,$id,$json);";
            cmd.Parameters.AddWithValue("$observation", observation.ObservationId);
            cmd.Parameters.AddWithValue("$id", intent.IntentId);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(observation, TwigJsonContext.Default.ConnectionRemoteWriteObservation));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (receipt is not null)
                return await AppendRemoteReceiptAsync(connection, tx, intent, receipt, ct).ConfigureAwait(false);
            return NativeResults.Ok();
        }, ct);

    internal Task<Result<bool>> AppendConnectionRemoteWriteReceiptAsync(ConnectionRemoteWriteIntent intent,
        ConnectionRemoteWriteReceipt receipt, CancellationToken ct = default)
        => ExecuteWriteAsync(async (connection, tx) =>
        {
            var original = await ValidateRemoteIntentAsync(connection, tx, intent, ct).ConfigureAwait(false);
            return original.IsSuccess ? await AppendRemoteReceiptAsync(connection, tx, intent, receipt, ct).ConfigureAwait(false) : original;
        }, ct);

    internal Task<Result<IReadOnlyList<ConnectionRemoteWriteHistory>>> ReadConnectionRemoteWritesAsync(
        string fingerprint, bool unsettledOnly = false, CancellationToken ct = default)
        => ExecuteReadAsync<IReadOnlyList<ConnectionRemoteWriteHistory>>(async connection =>
        {
            var intents = new List<(ConnectionRemoteWriteIntent Intent, ConnectionRemoteWriteReceipt? Receipt)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT i.intent_id,i.request_digest,i.intent_json,r.receipt_json FROM connection_remote_write_intents i LEFT JOIN connection_remote_write_receipts r ON r.intent_id=i.intent_id WHERE i.worktree_fingerprint=$fp"
                    + (unsettledOnly ? " AND r.intent_id IS NULL" : "") + " ORDER BY i.sequence;";
                cmd.Parameters.AddWithValue("$fp", fingerprint);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var intent = JsonSerializer.Deserialize(reader.GetString(2), TwigJsonContext.Default.ConnectionRemoteWriteIntent);
                    var receipt = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize(reader.GetString(3), TwigJsonContext.Default.ConnectionRemoteWriteReceipt);
                    if (intent is null || intent.Version != 1 || intent.IntentId != reader.GetString(0)
                        || intent.RequestDigest != reader.GetString(1) || intent.Fingerprint != fingerprint
                        || receipt is not null && (receipt.Version != 1 || receipt.IntentId != intent.IntentId || receipt.RequestDigest != intent.RequestDigest))
                        return NativeResults.Fail<IReadOnlyList<ConnectionRemoteWriteHistory>>("remote-write-native-evidence-inconsistent: preserve original records; unknown evidence is not settled.");
                    intents.Add((intent, receipt));
                }
            }
            var histories = new List<ConnectionRemoteWriteHistory>(intents.Count);
            foreach (var (intent, receipt) in intents)
            {
                var observations = new List<ConnectionRemoteWriteObservation>();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT observation_id,observation_json FROM connection_remote_write_observations WHERE intent_id=$id ORDER BY sequence;";
                cmd.Parameters.AddWithValue("$id", intent.IntentId);
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var observation = JsonSerializer.Deserialize(reader.GetString(1), TwigJsonContext.Default.ConnectionRemoteWriteObservation);
                    if (observation is null || observation.Version != 1 || observation.ObservationId != reader.GetString(0)
                        || observation.IntentId != intent.IntentId || observation.RequestDigest != intent.RequestDigest)
                        return NativeResults.Fail<IReadOnlyList<ConnectionRemoteWriteHistory>>("remote-write-observation-inconsistent: original outcome remains unknown.");
                    observations.Add(observation);
                }
                histories.Add(new(intent, observations, receipt));
            }
            return NativeResults.Ok<IReadOnlyList<ConnectionRemoteWriteHistory>>(histories);
        }, ct);

    private static async Task<Result<bool>> ValidateRemoteIntentAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionRemoteWriteIntent intent, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT intent_json FROM connection_remote_write_intents WHERE intent_id=$id AND request_digest=$digest AND worktree_fingerprint=$fp;";
        cmd.Parameters.AddWithValue("$id", intent.IntentId);
        cmd.Parameters.AddWithValue("$digest", intent.RequestDigest);
        cmd.Parameters.AddWithValue("$fp", intent.Fingerprint);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string == SerializeRemoteIntent(intent) ? NativeResults.Ok()
            : NativeResults.Fail("remote-write-intent-cas-mismatch: original immutable request/origin does not match.");
    }

    private static async Task<Result<bool>> AppendRemoteReceiptAsync(SqliteConnection connection, SqliteTransaction tx,
        ConnectionRemoteWriteIntent intent, ConnectionRemoteWriteReceipt receipt, CancellationToken ct)
    {
        if (receipt.IntentId != intent.IntentId || receipt.RequestDigest != intent.RequestDigest
            || !ConnectionRemoteWriteAdmission.SameAuthority(receipt.AuthorizingOrigin, intent.Origin))
            return NativeResults.Fail("remote-write-receipt-origin-mismatch: original principal/selection cannot acquire a different actor.");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO connection_remote_write_receipts(intent_id,receipt_id,receipt_json) VALUES($id,$receipt,$json) ON CONFLICT(intent_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("$id", intent.IntentId);
        cmd.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt, TwigJsonContext.Default.ConnectionRemoteWriteReceipt));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? NativeResults.Ok()
            : NativeResults.Fail("remote-write-receipt-conflict: an immutable native outcome already exists; inspect it, never rewrite history.");
    }

    private static string SerializeRemoteIntent(ConnectionRemoteWriteIntent intent)
        => JsonSerializer.Serialize(intent, TwigJsonContext.Default.ConnectionRemoteWriteIntent);
}
