using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

internal sealed record NativePlanReviewScope(NativePlanScope Scope, long StoreVersion,
    ImmutableArray<NativeEditorCapture> Captures, ImmutableArray<string> Previews, string? Proposal);
internal sealed record NativePlanReviewDocument(int Version, Guid Id, Guid UserInstance, long UserRevision,
    Guid BenchId, long BenchRevision, Guid PlanId, long PlanRevision, ImmutableArray<NativePlanReviewScope> Scopes);
internal sealed record NativePlanReviewReference(string Digest, NativePlanReviewDocument Document, bool Started, int Delivered, int Admitted);
internal sealed record NativePlanReviewLookup(string? Digest);

public sealed partial class UserBenchStore
{
    private async Task<Result<NativePlanReviewLookup>> LatestPlanReviewDigestAsync(Guid planId, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plan_review_references';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return Result<NativePlanReviewLookup>.Ok(new(null));
            command.CommandText = "SELECT digest FROM plan_review_references WHERE plan_id=$plan ORDER BY rowid DESC LIMIT 1;";
            command.Parameters.AddWithValue("$plan", planId.ToString("D"));
            return Result<NativePlanReviewLookup>.Ok(new(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string));
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanReviewLookup>(ex); }
    }

    internal static string PlanReviewDigest(NativePlanReviewDocument document)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(document, NativePlanReviewJson.Default.NativePlanReviewDocument))));

    internal async Task<Result<NativePlanReviewReference>> RetainPlanReviewAsync(NativePlanReviewDocument document, CancellationToken ct)
    {
        try
        {
            using var lease = AcquireAuthoringLease(document.BenchId);
            var library = await ReadAsync(ct).ConfigureAwait(false);
            var plan = await ReadPlanAsync(document.BenchId, ct).ConfigureAwait(false);
            if (!library.IsSuccess) return Result<NativePlanReviewReference>.Fail(library.Error);
            if (!plan.IsSuccess) return Result<NativePlanReviewReference>.Fail(plan.Error);
            if (library.Value.InstanceId != document.UserInstance || library.Value.Revision != document.UserRevision
                || !library.Value.Benches.Any(bench => bench.Id == document.BenchId && bench.Revision == document.BenchRevision)
                || !plan.Value.PlanMode || plan.Value.Id != document.PlanId || plan.Value.Revision != document.PlanRevision)
                return Result<NativePlanReviewReference>.Fail(ErrorCode.Conflict, "The whole Plan changed during review; no publication was authorized.");
            var json = JsonSerializer.Serialize(document, NativePlanReviewJson.Default.NativePlanReviewDocument);
            if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
                return Result<NativePlanReviewReference>.Fail(ErrorCode.InvalidInput, "The whole Plan exceeds the native review bound.");
            var digest = PlanReviewDigest(document);
            await using var db = await Open(ct).ConfigureAwait(false);
            using var tx = db.BeginTransaction(deferred: false);
            await using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS plan_review_references(
                    digest TEXT PRIMARY KEY,plan_id TEXT NOT NULL,document TEXT NOT NULL,
                    started INTEGER NOT NULL CHECK(started IN(0,1)),delivered INTEGER NOT NULL CHECK(delivered>=0),
                    admitted INTEGER NOT NULL CHECK(admitted>=delivered)) STRICT;
                SELECT digest,document FROM plan_review_references WHERE plan_id=$plan AND started=1;
                """;
            command.Parameters.AddWithValue("$plan", document.PlanId.ToString("D"));
            using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var previous = JsonSerializer.Deserialize(reader.GetString(1), NativePlanReviewJson.Default.NativePlanReviewDocument)
                        ?? throw new JsonException("Missing original review reference.");
                    if (PlanReviewDigest(previous) != reader.GetString(0))
                        throw new InvalidDataException("The original review reference integrity changed.");
                    for (var index = 0; index < previous.Scopes.Length; index++)
                    {
                        var scope = previous.Scopes[index];
                        var overlap = scope.Captures.Where(capture =>
                            document.Scopes.SelectMany(value => value.Captures).Any(current => current.Id == capture.Id)).ToImmutableArray();
                        if (overlap.IsEmpty)
                            continue;
                        var untouched = await ProveUnattempted(scope, overlap, ct).ConfigureAwait(false);
                        if (!untouched.IsSuccess) return Result<NativePlanReviewReference>.Fail(untouched.Error);
                    }
                }
            command.CommandText = """
                INSERT INTO plan_review_references VALUES($digest,$plan,$document,0,0,0)
                ON CONFLICT(digest) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$digest", digest);
            command.Parameters.AddWithValue("$document", json);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
            return Result<NativePlanReviewReference>.Ok(new(digest, document, false, 0, 0));
        }
        catch (ConnectionBindingResolutionException)
        { return Result<NativePlanReviewReference>.Fail(ErrorCode.Conflict, "An active native operation protects this Plan."); }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanReviewReference>(ex); }
    }

    internal async Task<Result<NativePlanReviewReference>> ReadPlanReviewAsync(string digest, CancellationToken ct)
    {
        try
        {
            if (digest.Length != 64 || digest.Any(character => !char.IsAsciiHexDigit(character)))
                return Result<NativePlanReviewReference>.Fail(ErrorCode.InvalidInput, "The exact native review digest is required.");
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='plan_review_references';";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                return Result<NativePlanReviewReference>.Fail(ErrorCode.NotFound, "Review the whole Plan first.");
            command.CommandText = "SELECT document,started,delivered,admitted FROM plan_review_references WHERE digest=$digest;";
            command.Parameters.AddWithValue("$digest", digest);
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return Result<NativePlanReviewReference>.Fail(ErrorCode.NotFound, "The exact whole-Plan review was not retained.");
            var document = JsonSerializer.Deserialize(reader.GetString(0), NativePlanReviewJson.Default.NativePlanReviewDocument)
                ?? throw new JsonException("Missing native review.");
            if (document.Version != 1 || document.Id == Guid.Empty || PlanReviewDigest(document) != digest
                || reader.GetInt32(2) > document.Scopes.Length || reader.GetInt32(3) > document.Scopes.Length
                || reader.GetInt32(3) < reader.GetInt32(2))
                throw new InvalidDataException("The original review reference is incompatible.");
            return Result<NativePlanReviewReference>.Ok(new(digest, document, reader.GetBoolean(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<NativePlanReviewReference>(ex); }
    }

    internal async Task<Result<bool>> AdvancePlanReviewAsync(NativePlanReviewReference expected, bool started, int delivered, int admitted, CancellationToken ct)
    {
        try
        {
            await using var db = await Open(ct).ConfigureAwait(false);
            await using var command = db.CreateCommand();
            command.CommandText = """
                UPDATE plan_review_references SET started=$started,delivered=$delivered,admitted=$admitted
                WHERE digest=$digest AND started=$beforeStarted AND delivered=$beforeDelivered AND admitted=$beforeAdmitted;
                """;
            command.Parameters.AddWithValue("$digest", expected.Digest);
            command.Parameters.AddWithValue("$started", started ? 1 : 0);
            command.Parameters.AddWithValue("$delivered", delivered);
            command.Parameters.AddWithValue("$admitted", admitted);
            command.Parameters.AddWithValue("$beforeStarted", expected.Started ? 1 : 0);
            command.Parameters.AddWithValue("$beforeDelivered", expected.Delivered);
            command.Parameters.AddWithValue("$beforeAdmitted", expected.Admitted);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Ok(true)
                : Result<bool>.Fail(ErrorCode.Conflict, "The original Apply reference changed. No operation was replayed.");
        }
        catch (Exception ex) when (StorageFailure(ex)) { return Failure<bool>(ex); }
    }

    private async Task<Result<bool>> ProveUnattempted(NativePlanReviewScope scope,
        ImmutableArray<NativeEditorCapture> overlap, CancellationToken ct)
    {
        var source = scope.Scope;
        var snapshot = await NativeCoreExecution.InspectAsync(new(source.StoreInstance, source.DatabasePath,
            source.OriginRoot, source.Source.Connection), ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<bool>.Fail(snapshot.Error);
        if (scope.Proposal is null) return Result<bool>.Ok(true);
        var proposal = ParseIntent(scope.Proposal);
        var keys = overlap.Select(capture => ParseIntent(capture.IntentJson).Operations.Single().Item).ToHashSet();
        var ordinals = proposal.Operations.Select((op, index) => (op, index))
            .Where(value => keys.Contains(value.op.Item)).Select(value => value.index).ToHashSet();
        var rows = snapshot.Value.Journal.Where(row => row.Digest == proposal.Digest && ordinals.Contains(row.Operation)).ToArray();
        if (rows.Any(row => row.Status != OpStatus.Planned && !DefinitivePlanEditNoEffect(snapshot.Value, row)))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "Original Core journal evidence fences this remainder.");
        if (captureRegistry is null)
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, "The original native registry is required to prove an untouched remainder.");
        using var registry = new SqliteSystemWorktreeRegistry(captureRegistry, TimeProvider.System);
        var claims = await registry.CoreAttemptReferencesAsync(source.StoreInstance, ct).ConfigureAwait(false);
        if (!claims.IsSuccess) return Result<bool>.Fail(claims.Error);
        var originalClaims = claims.Value.Where(claim => claim.Digest == proposal.Digest && ordinals.Contains(claim.Operation)).ToArray();
        if (!NativeCoreExecution.ReferencesMatch(snapshot.Value, originalClaims)
            || rows.Where(row => row.Status != OpStatus.Planned).Any(row => !originalClaims.Any(claim =>
                claim.Operation == row.Operation && claim.Attempt == row.Attempt)))
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, "Original Core/registry attempt attribution is incomplete; this remainder remains protected.");
        return Result<bool>.Ok(true);
    }

    internal static bool DefinitivePlanEditNoEffect(StoreSnapshot snapshot, JournalEntry row)
        => NativeCoreExecution.NoEffect(row) && snapshot.Proposals.Single(value => value.Digest == row.Digest)
            .Operations[row.Operation] is { Kind: OpKind.Batch, Item.IsLocal: false };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativePlanReviewDocument))]
internal sealed partial class NativePlanReviewJson : JsonSerializerContext;
