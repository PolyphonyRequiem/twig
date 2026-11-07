using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Twig.Core;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Services;

internal sealed record NativeEditorEndpoint(string Key, string? Revision, string DraftFingerprint);
internal sealed record NativeEditorCapture(Guid Id, long Revision, Guid UserInstance, Guid BenchId, long BenchRevision,
    Guid PlanId, long PlanRevision, bool PlanMode, Guid ScopeId, NativeSourceStamp Origin, string Root,
    Guid StoreInstance, long StoreVersion, string IntentJson, string DraftFingerprint, bool Ready, bool Settled = false)
{
    public ImmutableArray<NativeEditorEndpoint> Endpoints { get; init; } = [];
    public string ExecutionPath { get; init; } = "";
    public string BeforeFingerprint { get; init; } = "none";
    public bool Cancelled { get; init; }
    public string? SeedProposal { get; init; }
    internal bool SameAs(NativeEditorCapture other)
        => this with { Endpoints = other.Endpoints } == other && Endpoints.SequenceEqual(other.Endpoints);
}

public sealed partial class UserBenchStore
{
    private const int EditorCaptureDocumentBytes = 2 * 1024 * 1024;

    internal async Task<Result<NativeEditorCapture>> SaveEditorCaptureAsync(NativeEditorCapture capture, long expectedRevision, CancellationToken ct)
    {
        try
        {
            var document = JsonSerializer.Serialize(capture, NativeEditorJson.Default.NativeEditorCapture);
            if (Encoding.UTF8.GetByteCount(document) > EditorCaptureDocumentBytes)
                return Result<NativeEditorCapture>.Fail(ErrorCode.InvalidInput, "The serialized native editor capture exceeds its 2 MiB UTF-8 document bound.");
            using var namespaceLease = AcquireCaptureLease();
            using var lease = AcquireAuthoringLease(capture.BenchId);
            var generation = await CaptureGeneration(ct).ConfigureAwait(false);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: false);
            var library = await Read(db, tx, ct).ConfigureAwait(false);
            var bench = await Find(db, tx, capture.BenchId, ct).ConfigureAwait(false);
            var plan = await ReadPlan(db, tx, capture.BenchId, ct).ConfigureAwait(false);
            if (library.InstanceId != capture.UserInstance || bench is null || bench.Value.Deleted
                || bench.Value.Bench.Revision != capture.BenchRevision || plan.Id != capture.PlanId
                || plan.Revision != capture.PlanRevision || plan.PlanMode != capture.PlanMode
                || capture.PlanMode && !plan.Scopes.Any(scope => scope.Id == capture.ScopeId && scope.State == NativePlanScopeState.Ready
                    && scope.StoreInstance == capture.StoreInstance && scope.Source == capture.Origin && scope.OriginRoot == capture.Root)
                || !capture.PlanMode && (capture.ScopeId != Guid.Empty || capture.StoreInstance != capture.Origin.StoreInstance))
                return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "The captured Bench mode, scope, native source or revision changed; intent was preserved.");
            ValidatePlanOrigin(capture.Root, capture.Origin);
            var proposal = ParseIntent(capture.IntentJson);
            if (capture.Id == Guid.Empty || expectedRevision is < 0 or long.MaxValue || capture.Revision != expectedRevision + 1 || capture.StoreVersion < 0
                || proposal.Connection != capture.Origin.Connection || proposal.Operations.Length != 1
                || proposal.Operations[0].Kind is not (OpKind.Create or OpKind.Batch)
                || capture.Endpoints.IsDefault || capture.Endpoints.Length > 128 || capture.DraftFingerprint.Length != 64
                || Encoding.UTF8.GetByteCount(capture.IntentJson) > 1024 * 1024)
                return Result<NativeEditorCapture>.Fail(ErrorCode.InvalidInput, "An exact bounded native editor capture is required.");
            if (capture.SeedProposal is { } publication)
            {
                var original = ParseIntent(publication);
                if (capture.PlanMode || capture.Origin.Connection.Provider.Value != "ado"
                    || !capture.Ready || original.Connection != capture.Origin.Connection
                    || original.SchemaFingerprint != proposal.SchemaFingerprint || original.Operations.Length != 1
                    || original.Operations[0] is not { Kind: OpKind.Create, Item.IsLocal: true } effect
                    || effect.Item != proposal.Operations[0].Item || effect.Type != proposal.Operations[0].Type
                    || !effect.Fields.Equals(proposal.Operations[0].Fields) || effect.Other is not null
                    || effect.Relation is not null || effect.Resource is not null || !effect.Links.IsEmpty)
                    return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "An exact scalar normal ADO creation proposal is required.");
            }
            await EnsureCaptureStorage(db, tx, create: true, ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                SELECT revision FROM editor_captures WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", capture.Id.ToString("D"));
            var revision = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if ((revision is null ? 0 : Convert.ToInt64(revision)) != expectedRevision)
                return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "The durable editor draft version changed; no capture was overwritten.");
            command.CommandText = "SELECT document FROM editor_captures WHERE id=$id;";
            var previousText = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? "";
            if (previousText.Length != 0)
            {
                var previous = JsonSerializer.Deserialize(previousText, NativeEditorJson.Default.NativeEditorCapture)
                    ?? throw new JsonException("Missing native capture.");
                if (previous.Settled || previous.UserInstance != capture.UserInstance || previous.PlanMode != capture.PlanMode
                    || previous.StoreInstance != capture.StoreInstance || previous.ScopeId != capture.ScopeId || previous.Origin != capture.Origin
                    || previous.Root != capture.Root || previous.PlanMode && previous.PlanId != capture.PlanId
                    || !previous.PlanMode && proposal.Operations[0].Item.IsLocal && previous.BenchId != capture.BenchId
                    || previous.SeedProposal is not null && (previous.SeedProposal != capture.SeedProposal
                        || previous.IntentJson != capture.IntentJson || previous.DraftFingerprint != capture.DraftFingerprint
                        || previous.BenchId != capture.BenchId || previous.PlanId != capture.PlanId || capture.Cancelled)
                    || ParseIntent(previous.IntentJson).Operations[0].Item != proposal.Operations[0].Item)
                    return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "A durable capture cannot be retargeted or resurrect settled intent.");
            }
            command.CommandText = """
                INSERT INTO editor_captures VALUES($id,$bench,$plan,$store,$item,$revision,$document)
                ON CONFLICT(id) DO UPDATE SET bench_id=excluded.bench_id,plan_id=excluded.plan_id,
                    revision=excluded.revision,document=excluded.document;
                """;
            command.Parameters.AddWithValue("$bench", capture.BenchId.ToString("D"));
            command.Parameters.AddWithValue("$plan", capture.PlanMode ? capture.PlanId.ToString("D") : "");
            command.Parameters.AddWithValue("$store", capture.StoreInstance.ToString("D"));
            command.Parameters.AddWithValue("$item", CleanCoreAdoAccess.ItemText(proposal.Operations[0].Item));
            command.Parameters.AddWithValue("$revision", capture.Revision);
            command.Parameters.AddWithValue("$document", document);
            var reference = await ReserveCapture(capture, document, previousText, generation, ct).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await RecordCaptureHistory(db, tx, reference, document, previousText, ct).ConfigureAwait(false);
            command.CommandText = "UPDATE editor_capture_meta SET digest=$digest WHERE id=1;";
            command.Parameters.AddWithValue("$digest", await CaptureDigest(db, tx, ct).ConfigureAwait(false));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
            using var registry = new Twig.Infrastructure.Persistence.SqliteSystemWorktreeRegistry(captureRegistry!, TimeProvider.System);
            var confirmed = await registry.MarkCaptureAsync(reference.User, reference.Generation, reference.Head, false, null, ct).ConfigureAwait(false);
            if (!confirmed.IsSuccess) return Result<NativeEditorCapture>.Fail(confirmed.Error);
            return Result<NativeEditorCapture>.Ok(capture);
        }
        catch (ConnectionBindingResolutionException)
        { return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "Another native authoring operation owns this capture."); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativeEditorCapture>(ex); }
    }

    internal async Task<Result<ImmutableArray<NativeEditorCapture>>> ReadEditorCapturesAsync(CancellationToken ct, bool inspect = false)
    {
        try
        {
            using var namespaceLease = AcquireCaptureLease();
            await using var db = await Open(ct).ConfigureAwait(false);
            if (!inspect) _ = await CaptureGeneration(ct).ConfigureAwait(false);
            using var command = db.CreateCommand();
            using var tx = db.BeginTransaction(deferred: false);
            command.Transaction = tx;
            if (!await EnsureCaptureStorage(db, tx, create: false, ct).ConfigureAwait(false))
                return Result<ImmutableArray<NativeEditorCapture>>.Ok([]);
            command.CommandText = "SELECT id,revision,document FROM editor_captures ORDER BY id;";
            var captures = ImmutableArray.CreateBuilder<NativeEditorCapture>();
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var text = reader.GetString(2);
                if (Encoding.UTF8.GetByteCount(text) > EditorCaptureDocumentBytes || captures.Count >= 1024)
                    throw new InvalidDataException("Native editor capture bounds exceeded.");
                var capture = JsonSerializer.Deserialize(text, NativeEditorJson.Default.NativeEditorCapture)
                    ?? throw new JsonException("Missing native editor capture.");
                if (capture.Id.ToString("D") != reader.GetString(0) || capture.Revision != reader.GetInt64(1))
                    throw new InvalidDataException("Native editor ownership row disagrees.");
                _ = ParseIntent(capture.IntentJson);
                captures.Add(capture);
            }
            reader.Close();
            tx.Commit();
            return Result<ImmutableArray<NativeEditorCapture>>.Ok(captures.ToImmutable());
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<ImmutableArray<NativeEditorCapture>>(ex); }
    }

    internal static Proposal ParseIntent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CoreProposalFiles.Parse(document.RootElement);
    }

    private static async Task<bool> EnsureCaptureStorage(SqliteConnection db, SqliteTransaction tx, bool create, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "PRAGMA table_info(meta);";
        var hasWitness = false;
        using (var columns = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await columns.ReadAsync(ct).ConfigureAwait(false))
                hasWitness |= columns.GetString(1) == "editor_initialized";
        var initialized = false;
        if (hasWitness)
        {
            command.CommandText = "SELECT editor_initialized FROM meta WHERE id=1;";
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long value || value is < 0 or > 1)
                throw new InvalidDataException("Native capture initialization provenance is invalid.");
            initialized = value == 1;
        }
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN('editor_captures','editor_capture_meta');";
        var tables = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
        if (tables == 1 || initialized && tables != 2)
            throw new InvalidDataException("Previously initialized native capture storage is missing; relationship intent was not treated as absent.");
        if (tables == 2) await CheckCaptureSeal(db, tx, ct).ConfigureAwait(false);
        else
        {
            if (!create) return false;
            command.CommandText = """
                CREATE TABLE editor_captures(id TEXT PRIMARY KEY,bench_id TEXT NOT NULL,plan_id TEXT NOT NULL,
                    store_instance TEXT NOT NULL,item TEXT NOT NULL,revision INTEGER NOT NULL,document TEXT NOT NULL);
                CREATE TABLE editor_capture_meta(id INTEGER PRIMARY KEY CHECK(id=1),digest TEXT NOT NULL);
                INSERT INTO editor_capture_meta VALUES(1,'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (!hasWitness)
        {
            command.CommandText = "ALTER TABLE meta ADD COLUMN editor_initialized INTEGER NOT NULL DEFAULT 0 CHECK(editor_initialized IN(0,1));";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (!initialized)
        {
            command.CommandText = "UPDATE meta SET editor_initialized=1 WHERE id=1;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return true;
    }

    private static async Task CheckCaptureSeal(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT digest FROM editor_capture_meta WHERE id=1;";
        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != await CaptureDigest(db, tx, ct).ConfigureAwait(false))
            throw new InvalidDataException("Durable native capture integrity changed; intent was not repaired or treated as absent.");
    }

    private static async Task<string> CaptureDigest(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,revision,document FROM editor_captures ORDER BY id;";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (++count > 1024 || Encoding.UTF8.GetByteCount(reader.GetString(2)) > EditorCaptureDocumentBytes)
                throw new InvalidDataException("Native capture bounds exceeded.");
            hash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new[] { reader.GetString(0), reader.GetInt64(1).ToString(System.Globalization.CultureInfo.InvariantCulture), reader.GetString(2) },
                NativeCoreExecutionJson.Default.StringArray)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativeEditorCapture))]
internal sealed partial class NativeEditorJson : JsonSerializerContext;
