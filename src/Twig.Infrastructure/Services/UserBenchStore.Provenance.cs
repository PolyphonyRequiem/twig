using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

public sealed partial class UserBenchStore
{
    private IDisposable AcquireCaptureLease()
        => ConnectionOperationGate.AcquireCoreTargets(path, [CaptureLeaseKey(path)]);
    internal static string CaptureLeaseKey(string path) => "native-user-captures:" + WorkspaceKey(path);
    private static string CaptureHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task<Result<bool>> CheckGenerationAsync(NativeCaptureHead head, CancellationToken ct)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = head.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA application_id;";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != ApplicationId)
                return Result<bool>.Fail(ErrorCode.StorageUnavailable, "The referenced native user store was replaced.");
            command.CommandText = "SELECT instance,editor_registry,editor_generation,editor_head FROM meta WHERE id=1;";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetString(0) != head.User.ToString("D")
                || reader.IsDBNull(1) || reader.GetString(1) != head.Registry.ToString("N")
                || reader.GetInt64(2) != head.Generation || reader.GetString(3) != head.Head
                || head.Birth == "reserved" || head.Birth.StartsWith("migrating:", StringComparison.Ordinal))
                return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Independent native user/capture generation disagrees; no empty-state or rewind inference.");
            reader.Close();
            if (head.Generation != 0)
            {
                command.CommandText = "SELECT COUNT(*) FROM editor_capture_history;";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != head.Generation)
                    return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Immutable native generation history is incomplete or unreferenced.");
                command.CommandText = """
                    SELECT COUNT(*) FROM editor_captures c WHERE NOT EXISTS(
                        SELECT 1 FROM editor_capture_history h WHERE h.document=c.document);
                    """;
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                    return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Active capture has no independently referenced history.");
                command.CommandText = """
                    SELECT COUNT(*) FROM editor_capture_history h WHERE h.generation=(
                        SELECT MAX(p.generation) FROM editor_capture_history p
                        WHERE json_extract(p.document,'$.Id')=json_extract(h.document,'$.Id'))
                    AND NOT EXISTS(SELECT 1 FROM editor_captures c WHERE c.document=h.document);
                    """;
                if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) != 0)
                    return Result<bool>.Fail(ErrorCode.StorageUnavailable, "A referenced active capture was lost or rewound.");
            }
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    private async Task<NativeCaptureHead> CaptureGeneration(CancellationToken ct)
    {
        if (captureRegistry is null) throw new InvalidDataException("Native capture authoring requires its exact independent system registry.");
        using var registry = new SqliteSystemWorktreeRegistry(captureRegistry, TimeProvider.System);
        var heads = await registry.CaptureHeadsAsync(ct).ConfigureAwait(false);
        if (!heads.IsSuccess) throw new InvalidDataException(heads.Error.Message);
        var head = heads.Value.SingleOrDefault(value => NativePathComparer.Instance.Equals(value.Path, path))
            ?? throw new InvalidDataException("Historical native user namespace requires actual one-time provenance inventory; empty is not first-use proof.");
        var valid = await CheckGenerationAsync(head, ct).ConfigureAwait(false);
        if (!valid.IsSuccess) throw new InvalidDataException(valid.Error.Message);
        var history = await registry.CaptureInventoryAsync(ct).ConfigureAwait(false);
        if (!history.IsSuccess) throw new InvalidDataException(history.Error.Message);
        return head;
    }

    internal async Task<Result<string>> CaptureInventoryEvidenceAsync(IEnumerable<NativeCoreStoreReference> sources, CancellationToken ct)
    {
        try
        {
            using var lease = AcquireCaptureLease();
            await using var db = await Open(ct).ConfigureAwait(false);
            using var registry = new SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
            var heads = await registry.CaptureHeadsAsync(ct).ConfigureAwait(false);
            if (!heads.IsSuccess) return Result<string>.Fail(heads.Error);
            if (heads.Value.Any(value => NativePathComparer.Instance.Equals(value.Path, path))) return await registry.CaptureInventoryAsync(ct).ConfigureAwait(false);
            var captures = await ReadEditorCapturesAsync(ct, inspect: true).ConfigureAwait(false);
            if (!captures.IsSuccess) return Result<string>.Fail(captures.Error);
            if (captures.Value.IsEmpty || captures.Value.Any(capture => !capture.Ready))
                return Result<string>.Fail(ErrorCode.StorageUnavailable, "Historical namespace lacks actual sealed Ready payload/history; completeness cannot fabricate it.");
            foreach (var capture in captures.Value.Where(value => !value.Settled))
            {
                var source = sources.SingleOrDefault(value => value.Instance == capture.StoreInstance && value.Root == capture.Root
                    && value.Connection == capture.Origin.Connection);
                if (source is null) return Result<string>.Fail(ErrorCode.StorageUnavailable, "Legacy capture has no exact execution source.");
                var stored = await NativeCoreExecution.InspectAsync(source, ct).ConfigureAwait(false);
                if (!stored.IsSuccess) return Result<string>.Fail(stored.Error);
                var key = ParseIntent(capture.IntentJson).Operations.Single().Item;
                if (CoreWorkspace.Fingerprint(source.Connection, stored.Value.Drafts.SingleOrDefault(value => value.Key == key)) != capture.DraftFingerprint)
                    return Result<string>.Fail(ErrorCode.Conflict, "Legacy capture differs from authoritative protected draft.");
            }
            using var tx = db.BeginTransaction(deferred: true);
            return Result<string>.Ok("legacy:" + await CaptureDigest(db, tx, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<string>(ex); }
    }

    internal async Task<Result<bool>> MigrateCaptureProvenanceAsync(IEnumerable<NativeCoreStoreReference> sources, CancellationToken ct)
    {
        try
        {
            using var lease = AcquireCaptureLease();
            using var registry = new SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
            var heads = await registry.CaptureHeadsAsync(ct).ConfigureAwait(false);
            if (!heads.IsSuccess) return Result<bool>.Fail(heads.Error);
            if (heads.Value.Any(value => NativePathComparer.Instance.Equals(value.Path, path))) return Result<bool>.Ok(true);
            var evidence = await CaptureInventoryEvidenceAsync(sources, ct).ConfigureAwait(false);
            if (!evidence.IsSuccess) return Result<bool>.Fail(evidence.Error);
            await using var db = await Open(ct).ConfigureAwait(false);
            var user = (await ReadAsync(ct).ConfigureAwait(false)).Value.InstanceId;
            var identity = (await registry.ReadCoreGateAsync(ct).ConfigureAwait(false)).Value.Instance;
            var registered = await registry.MigrateCaptureUserAsync(path, user, evidence.Value, ct).ConfigureAwait(false);
            if (!registered.IsSuccess) return registered;
            using (var tx = db.BeginTransaction(deferred: false))
            {
                using var command = db.CreateCommand(); command.Transaction = tx;
                command.CommandText = """
                    ALTER TABLE meta ADD COLUMN editor_registry TEXT;
                    ALTER TABLE meta ADD COLUMN editor_generation INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE meta ADD COLUMN editor_head TEXT NOT NULL DEFAULT '';
                    UPDATE meta SET editor_registry=$registry WHERE id=1;
                    """;
                command.Parameters.AddWithValue("$registry", identity.ToString("N"));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); tx.Commit();
            }
            using var rows = db.CreateCommand(); rows.CommandText = "SELECT document FROM editor_captures ORDER BY id;";
            var documents = new List<string>();
            using (var reader = await rows.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) documents.Add(reader.GetString(0));
            long generation = 0; var parent = "";
            foreach (var document in documents)
            {
                var capture = JsonSerializer.Deserialize(document, NativeEditorJson.Default.NativeEditorCapture)!;
                var source = sources.Single(value => value.Instance == capture.StoreInstance && value.Root == capture.Root
                    && value.Connection == capture.Origin.Connection);
                var reference = new NativeCaptureReference(path, user, ++generation, parent, capture.Id, capture.Revision,
                    CaptureHash(document), "", source.DatabasePath, capture.Root, capture.Origin, capture.StoreInstance,
                    capture.StoreVersion, "unavailable-legacy", capture.DraftFingerprint) { Migration = evidence.Value };
                var reserved = await registry.ReserveCaptureAsync(reference, ct).ConfigureAwait(false);
                if (!reserved.IsSuccess) return Result<bool>.Fail(reserved.Error);
                using var tx = db.BeginTransaction(deferred: false);
                await RecordCaptureHistory(db, tx, reference, document, "", ct).ConfigureAwait(false); tx.Commit();
                var confirmed = await registry.MarkCaptureAsync(user, generation, reference.Head, false, null, ct).ConfigureAwait(false);
                if (!confirmed.IsSuccess) return confirmed;
                parent = reference.Head;
            }
            return await registry.CompleteCaptureMigrationAsync(user, evidence.Value, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    internal static async Task<Result<bool>> CheckHistoryAsync(NativeCaptureReference reference, CancellationToken ct)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = reference.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT document,before_document FROM editor_capture_history WHERE generation=$generation AND head=$head;";
            command.Parameters.AddWithValue("$generation", reference.Generation); command.Parameters.AddWithValue("$head", reference.Head);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || CaptureHash(reader.GetString(0)) != reference.Document
                || (reader.GetString(1).Length == 0 ? "" : CaptureHash(reader.GetString(1))) != reference.Previous)
                return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Immutable native capture history is absent or mismatched; it cannot be reconstructed.");
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    private async Task<NativeCaptureReference> ReserveCapture(NativeEditorCapture capture, string document, string previous,
        NativeCaptureHead head, CancellationToken ct)
    {
        var reference = new NativeCaptureReference(head.Path, capture.UserInstance, head.Generation + 1, head.Head,
            capture.Id, capture.Revision, CaptureHash(document), previous.Length == 0 ? "" : CaptureHash(previous),
            capture.ExecutionPath, capture.Root, capture.Origin, capture.StoreInstance, capture.StoreVersion,
            capture.BeforeFingerprint, capture.DraftFingerprint);
        using var registry = new SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
        if (reference.ExecutionPath.Length == 0)
        {
            var gate = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
            if (!gate.IsSuccess) throw new InvalidDataException(gate.Error.Message);
            var source = gate.Value.Sources.SingleOrDefault(value => value.Instance == capture.StoreInstance
                && value.Root == capture.Root && value.Connection == capture.Origin.Connection)
                ?? throw new InvalidDataException("The legacy capture's exact source is unavailable.");
            reference = reference with { ExecutionPath = source.DatabasePath };
        }
        var reserved = await registry.ReserveCaptureAsync(reference, ct).ConfigureAwait(false);
        if (!reserved.IsSuccess) throw new InvalidDataException(reserved.Error.Message);
        return reserved.Value;
    }

    private static async Task RecordCaptureHistory(SqliteConnection db, SqliteTransaction tx, NativeCaptureReference reference,
        string document, string previous, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS editor_capture_history(generation INTEGER PRIMARY KEY,head TEXT NOT NULL,
                document TEXT NOT NULL,before_document TEXT NOT NULL);
            INSERT INTO editor_capture_history VALUES($generation,$head,$document,$before);
            UPDATE meta SET editor_generation=$generation,editor_head=$head WHERE id=1;
            """;
        command.Parameters.AddWithValue("$generation", reference.Generation); command.Parameters.AddWithValue("$head", reference.Head);
        command.Parameters.AddWithValue("$document", document); command.Parameters.AddWithValue("$before", previous);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task<Result<bool>> AdmitCaptureStageAsync(NativeEditorCapture capture, long? actual, CancellationToken ct)
    {
        try
        {
            using var lease = AcquireCaptureLease();
            var head = await CaptureGeneration(ct).ConfigureAwait(false);
            var history = await CaptureHistory(head, capture, ct).ConfigureAwait(false);
            if (!history.Capture.SameAs(capture)) return Result<bool>.Fail(ErrorCode.Conflict, "The exact pending capture changed before native stage admission/readback.");
            using var registry = new SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
            return await registry.MarkCaptureAsync(head.User, history.Generation, history.Head, true, actual, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    private async Task<(NativeEditorCapture Capture, string Before, long Generation, string Head)> CaptureHistory(
        NativeCaptureHead head, NativeEditorCapture capture, CancellationToken ct)
    {
        await using var db = await Open(ct).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT document,before_document,generation,head FROM editor_capture_history
            WHERE json_extract(document,'$.Id')=$capture AND json_extract(document,'$.Revision')=$revision;
            """;
        command.Parameters.AddWithValue("$capture", capture.Id.ToString("D")); command.Parameters.AddWithValue("$revision", capture.Revision);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidDataException("The exact immutable native capture payload is missing.");
        var text = reader.GetString(0);
        return (JsonSerializer.Deserialize(text, NativeEditorJson.Default.NativeEditorCapture)
            ?? throw new JsonException("Missing native capture history."), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
    }

    internal async Task<Result<NativeEditorCapture>> CancelCaptureAsync(NativeEditorCapture pending, CancellationToken ct)
    {
        try
        {
            var head = await CaptureGeneration(ct).ConfigureAwait(false);
            var history = await CaptureHistory(head, pending, ct).ConfigureAwait(false);
            if (!history.Capture.SameAs(pending) || pending.Ready || pending.Settled)
                return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "Only the exact interrupted local capture can be cancelled.");
            var before = history.Before.Length == 0 ? pending with { Cancelled = true } :
                JsonSerializer.Deserialize(history.Before, NativeEditorJson.Default.NativeEditorCapture)
                    ?? throw new JsonException("Missing prior Ready capture.");
            return await SaveEditorCaptureAsync(before with { Revision = pending.Revision + 1,
                BenchId = pending.BenchId, BenchRevision = pending.BenchRevision,
                PlanId = pending.PlanId, PlanRevision = pending.PlanRevision }, pending.Revision, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativeEditorCapture>(ex); }
    }

    internal async Task<Result<bool>> ConfirmInterruptedHistoryAsync(CancellationToken ct)
    {
        try
        {
            using var lease = AcquireCaptureLease();
            using var registry = new SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
            var heads = await registry.CaptureHeadsAsync(ct).ConfigureAwait(false);
            if (!heads.IsSuccess) return Result<bool>.Fail(heads.Error);
            var head = heads.Value.SingleOrDefault(value => NativePathComparer.Instance.Equals(value.Path, path));
            if (head is null) return Result<bool>.Fail(ErrorCode.StorageUnavailable, "No independent namespace reference exists.");
            var valid = await CheckGenerationAsync(head, ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return valid;
            var progress = await registry.CaptureProgressAsync(head.User, head.Generation, ct).ConfigureAwait(false);
            if (!progress.IsSuccess) return Result<bool>.Fail(progress.Error);
            var payload = await CheckHistoryAsync(progress.Value.Reference, ct).ConfigureAwait(false);
            if (!payload.IsSuccess) return payload;
            return await registry.MarkCaptureAsync(head.User, head.Generation, head.Head, false, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    internal static async Task<Result<bool>> CheckActiveCapturesAsync(NativeCaptureHead head,
        IEnumerable<NativeCoreStoreReference> sources, CancellationToken ct)
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = head.Path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync(ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='editor_captures' AND type='table';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return head.Generation == 0 ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.StorageUnavailable, "Capture storage was lost.");
            command.CommandText = "SELECT document FROM editor_captures;";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var capture = JsonSerializer.Deserialize(reader.GetString(0), NativeEditorJson.Default.NativeEditorCapture)!;
                if (!capture.Ready && !capture.Cancelled && !capture.Settled)
                    return Result<bool>.Fail(ErrorCode.Conflict, "An interrupted local capture requires explicit recovery before publication.");
                if (capture.Cancelled || capture.Settled) continue;
                var source = sources.SingleOrDefault(value => value.Instance == capture.StoreInstance && value.Root == capture.Root
                    && value.Connection == capture.Origin.Connection);
                if (source is null) return Result<bool>.Fail(ErrorCode.StorageUnavailable, "The referenced execution store is missing.");
                var stored = await NativeCoreExecution.InspectAsync(source, ct).ConfigureAwait(false);
                if (!stored.IsSuccess) return Result<bool>.Fail(stored.Error);
                var key = ParseIntent(capture.IntentJson).Operations.Single().Item;
                if (stored.Value.Version < capture.StoreVersion
                    || CoreWorkspace.Fingerprint(source.Connection, stored.Value.Drafts.SingleOrDefault(value => value.Key == key)) != capture.DraftFingerprint)
                    return Result<bool>.Fail(ErrorCode.Conflict, "Protected native draft/version differs from its capture; review is required before publication.");
            }
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }
}
