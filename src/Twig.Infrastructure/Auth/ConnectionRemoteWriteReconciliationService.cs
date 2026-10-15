using System.Globalization;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

internal sealed record ConnectionRemoteWriteInspection(string State, string IntentId, string Digest, bool CanReconcile,
    string WorktreeRoot, string BindingId, string IdentityId, ConnectionRemoteWriteRequest Request,
    IReadOnlyList<ConnectionRemoteWriteObservation> Observations, ConnectionRemoteWriteReceipt? Receipt,
    IReadOnlyList<string> Blockers, IReadOnlyList<string> NextSteps);

internal interface IConnectionRemoteWriteReconciliationService
{
    Task<IReadOnlyList<ConnectionRemoteWriteInspection>> InspectAsync(TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default);
    Task<ConnectionRemoteWriteInspection> ReconcileAsync(TwigConfiguration configuration, TwigPaths paths,
        string intentId, string confirmedDigest, string authorizer, string rationale, CancellationToken ct = default);
}

/// <summary>Read-only evidence collection and native append-only settlement; it never repeats a remote mutation.</summary>
internal sealed class ConnectionRemoteWriteReconciliationService : IConnectionRemoteWriteReconciliationService, IDisposable
{
    private readonly IConnectionBindingService _bindings;
    private readonly SqliteSystemWorktreeRegistry _registry;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    internal ConnectionRemoteWriteReconciliationService(string userHome, IConnectionBindingService bindings, HttpClient? readbackHttpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userHome);
        if (!Path.IsPathFullyQualified(userHome)) throw new ArgumentException("userHome must be absolute.", nameof(userHome));
        var registryPath = Path.Combine(Path.GetFullPath(userHome), "system.db");
        if (!SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(registryPath, Path.GetFullPath(bindings.RegistryPath)))
            throw new ArgumentException("Native reconciliation must use the binding service's registry.", nameof(bindings));
        _bindings = bindings;
        _registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        _ownsHttp = readbackHttpClient is null;
        _http = readbackHttpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    public async Task<IReadOnlyList<ConnectionRemoteWriteInspection>> InspectAsync(TwigConfiguration configuration, TwigPaths paths, CancellationToken ct = default)
    {
        var current = await _bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        var histories = await _registry.ReadConnectionRemoteWritesAsync(current.Operation.WorktreeFingerprint, ct: ct).ConfigureAwait(false);
        Require(histories);
        return histories.Value.Select(history => Report(history,
            ConnectionRemoteWriteAdmission.SameAuthority(current, history.Intent.Origin) ? []
                : ["remote-write-origin-changed: reconnect the original binding/principal before native evidence collection."])).ToArray();
    }

    public async Task<ConnectionRemoteWriteInspection> ReconcileAsync(TwigConfiguration configuration, TwigPaths paths,
        string intentId, string confirmedDigest, string authorizer, string rationale, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir ?? paths.TwigDir, out var anchor, out var failure))
            throw new InvalidOperationException("remote-write-worktree-required: " + failure);
        // A live operation can still return its real acknowledgment; reconciliation cannot race it.
        using var fence = ConnectionOperationGate.Acquire(anchor.WorktreeRoot, exclusive: true);
        var current = await _bindings.ResolveAsync(configuration, paths, ct).ConfigureAwait(false);
        var histories = await _registry.ReadConnectionRemoteWritesAsync(current.Operation.WorktreeFingerprint, ct: ct).ConfigureAwait(false);
        Require(histories);
        var history = histories.Value.SingleOrDefault(row => row.Intent.IntentId == intentId)
            ?? throw new InvalidOperationException("remote-write-intent-not-found: inspect this originating checkout's native records.");
        var intent = history.Intent;
        if (intent.RequestDigest != confirmedDigest || intent.RequestDigest != ConnectionRemoteWriteAdmission.RequestDigest(intent.Origin, intent.Request))
            throw new InvalidOperationException("remote-write-digest-mismatch: confirm the exact immutable native request; no receipt was written.");
        if (!ConnectionRemoteWriteAdmission.SameAuthority(current, intent.Origin) || authorizer != intent.Origin.Identity.IdentityId)
            return Report(history, ["remote-write-authority-mismatch: authorization must identify the original registered identity id, with its unchanged binding/principal/admitted generation. No current or display-name actor can adopt this intent."]);
        if (history.Receipt is not null)
        {
            if (intent.Request.EffectKind != "workitem-create" || intent.Request.SeedCorrelation is not { } settledCorrelation
                || !ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(intent.Request.Payload, settledCorrelation)
                || MigrationBoundAuthenticationProvider.PublishedIdFromReceipt(history) is null)
                return Report(history, []);
            var cleanupProvider = _bindings.CreateAuthenticationProvider(current);
            try { return await CleanupConfirmedSeedAsync(history, current, cleanupProvider, ct).ConfigureAwait(false); }
            finally { (cleanupProvider as IDisposable)?.Dispose(); }
        }
        if (intent.Request.SeedCorrelation is not null && intent.Request.EffectKind == "workitem-create")
            return await ReconcilePublishedSeedAsync(history, current, configuration, paths, authorizer, rationale, ct).ConfigureAwait(false);
        if (!TryRevisionFence(intent.Request, out var expectedRevision))
            return Report(history, ["remote-write-attribution-unavailable: lost unrevisioned POST/DELETE or unsupported mutation has no server-issued attributable outcome. Matching text, actor or current state is not proof that the original request cannot still complete. Preserve this intent; there is no force, automatic replay, retirement or expiry."]);

        var provider = _bindings.CreateAuthenticationProvider(current);
        try
        {
            using var operation = await ConnectionOperationAdmission.AcquireAsync(provider, ct).ConfigureAwait(false);
            var token = await provider.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var target = new Uri(intent.Request.Target);
            if (target.UserInfo.Length != 0 || target.Fragment.Length != 0 || !ConnectionRemoteWriteAdmission.IsWithinAuthority(current, target))
                return Report(history, ["remote-write-target-authority-mismatch: recorded target is outside the original declared authority; no credential is sent and no outcome is settled."]);
            var currentUri = new UriBuilder(target) { Query = "$expand=all&api-version=7.1" }.Uri;
            using var latest = await ReadAsync(currentUri, token, ct).ConfigureAwait(false);
            if (!TryWorkItemRevision(latest.RootElement, ConnectionRemoteWriteAdmission.TargetWorkItemId(intent.Request.Target), out var currentRevision)
                || currentRevision <= expectedRevision)
                return Report(history, ["remote-write-revision-not-exhausted: authoritative current revision does not prove the original CAS cannot still win; outcome remains unknown."]);
            var effectUri = new UriBuilder(target)
            {
                Path = target.AbsolutePath.TrimEnd('/') + "/revisions/" + checked(expectedRevision + 1).ToString(CultureInfo.InvariantCulture),
                Query = "$expand=all&api-version=7.1"
            }.Uri;
            using var effect = await ReadAsync(effectUri, token, ct).ConfigureAwait(false);
            if (!TryWorkItemRevision(effect.RootElement, ConnectionRemoteWriteAdmission.TargetWorkItemId(intent.Request.Target), out var effectRevision)
                || effectRevision != expectedRevision + 1 || !MatchesExpectedEffects(intent.Request.Payload, effect.RootElement))
                return Report(history, ["remote-write-effect-unproved: the immutable first post-CAS revision does not establish this exact requested effect. A newer successful mutation or matching current state cannot settle it."]);
            var receipt = new ConnectionRemoteWriteReceipt(1, Guid.NewGuid().ToString("N"), intent.IntentId,
                intent.RequestDigest, "exhausted-cas-readback", null, effect.RootElement.GetRawText(), current,
                authorizer, rationale, DateTimeOffset.UtcNow);
            var appended = await _registry.AppendConnectionRemoteWriteReceiptAsync(intent, receipt, ct).ConfigureAwait(false);
            if (!appended.IsSuccess) throw new InvalidOperationException(appended.Error);
            return Report(history with { Receipt = receipt }, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Report(history, ["remote-write-readback-unavailable: " + ex.Message + " Original uncertainty remains durable; no mutation was replayed."]); }
        finally { (provider as IDisposable)?.Dispose(); }
    }

    private async Task<ConnectionRemoteWriteInspection> ReconcilePublishedSeedAsync(ConnectionRemoteWriteHistory history,
        ResolvedConnectionBinding current, TwigConfiguration configuration, TwigPaths paths,
        string authorizer, string rationale, CancellationToken ct)
    {
        var intent = history.Intent;
        var correlation = intent.Request.SeedCorrelation!;
        var provider = _bindings.CreateAuthenticationProvider(current);
        try
        {
            var descriptionOrigin = ConnectionRemoteWriteAdmission.SeedCreateHasDescriptionCorrelation(intent.Request.Payload, correlation);
            if (!descriptionOrigin && !ConnectionRemoteWriteAdmission.SeedCreateHasCorrelation(intent.Request.Payload, correlation.Tag))
                return Report(history, ["remote-write-seed-correlation-unsupported: original create carried no exact Description origin or historical correlation tag. Shared tag/title/type/time cannot establish this POST outcome; preserve uncertainty."]);
            using var operation = await ConnectionOperationAdmission.AcquireAsync(provider, ct).ConfigureAwait(false);
            var currentPaths = TwigPaths.BuildPaths(paths.TwigDir, configuration, paths.StartDir);
            using var store = SqliteCacheStore.OpenWorkspace(currentPaths, _bindings.RegistryPath);
            var publishedIntent = await new SqlitePublishIntentRepository(store).GetIntentAsync(correlation.Identity, ct).ConfigureAwait(false);
            var map = new SqlitePublishIdMapRepository(store, new SqliteStagedIdentityRegistry(store));
            var publishedId = await map.GetNewIdAsync(correlation.Identity, ct).ConfigureAwait(false);
            if (publishedIntent is null || publishedIntent.Identity != correlation.Identity
                || publishedIntent.RecordedAt != correlation.IntentRecordedAt || publishedIntent.PublishedId is not { } id
                || publishedId != id || publishedIntent.CompletedAt is not { } completedAt || completedAt < intent.AdmittedAt
                || publishedIntent.Title != ConnectionRemoteWriteAdmission.SeedCreateTitle(intent.Request.Payload)
                || publishedIntent.TypeName != ConnectionRemoteWriteAdmission.SeedCreateType(intent.Request.Target))
                return Report(history, ["remote-write-seed-native-outcome-unproved: the exact original staged identity/intent timestamp, completed publish intent and durable ID map do not establish this create outcome. Native publish recovery must finish separately; no unrelated mapped or Verified seed can settle it."]);
            var unresolved = await new SqlitePlanJournalRepository(store).GetUnresolvedAsync(ct).ConfigureAwait(false);
            foreach (var journal in unresolved)
                foreach (var row in journal.Operations)
                {
                    using var request = JsonDocument.Parse(row.RequestJson);
                    if (request.RootElement.TryGetProperty("stagedIdentity", out var staged)
                        && staged.ValueKind == JsonValueKind.String && staged.GetString() == correlation.Identity.ToString()
                        && row.State != Twig.Domain.Services.Plan.PlanOperationState.Verified && row.OutcomeReceipt is null)
                        return Report(history, ["remote-write-seed-proposal-unsettled: the exact original staged seed still has an unresolved native proposal operation; finish its original native readback/reconciliation first."]);
                }
            var target = new Uri(intent.Request.Target);
            if (!ConnectionRemoteWriteAdmission.IsWithinAuthority(current, target))
                return Report(history, ["remote-write-seed-target-mismatch: native create authority no longer matches the original binding."]);
            var uri = new UriBuilder(target)
            {
                Path = target.AbsolutePath[..(target.AbsolutePath.LastIndexOf('/') + 1)] + id.ToString(CultureInfo.InvariantCulture) + "/revisions/1",
                Query = "$expand=all&api-version=7.1"
            }.Uri;
            var token = await provider.GetAccessTokenAsync(ct).ConfigureAwait(false);
            using var creation = await ReadAsync(uri, token, ct).ConfigureAwait(false);
            if (!TryWorkItemRevision(creation.RootElement, id, out var revision) || revision != 1
                || !creation.RootElement.TryGetProperty("fields", out var fields)
                || fields.ValueKind != JsonValueKind.Object
                || !fields.TryGetProperty("System.WorkItemType", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != publishedIntent.TypeName
                || !fields.TryGetProperty("System.CreatedDate", out var date) || date.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt)
                || (!descriptionOrigin && createdAt < correlation.IntentRecordedAt)
                || !(descriptionOrigin
                    ? fields.TryGetProperty("System.Description", out var description) && description.ValueKind == JsonValueKind.String
                        && correlation.ContainsDescriptionMarker(description.GetString())
                    : fields.TryGetProperty("System.Tags", out var tags) && tags.ValueKind == JsonValueKind.String
                        && (tags.GetString() ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Contains(correlation.Tag, StringComparer.Ordinal))
                || !MatchesExpectedEffects(intent.Request.Payload, creation.RootElement, hasRevisionTest: false))
                return Report(history, ["remote-write-seed-creation-unproved: original-bound immutable revision one does not prove the exact original create payload/type/time effect. Current text, latest state and unrelated success are not attribution."]);
            var receipt = new ConnectionRemoteWriteReceipt(1, Guid.NewGuid().ToString("N"), intent.IntentId,
                intent.RequestDigest, "native-published-seed-readback", null,
                SeedReceiptEvidence(publishedIntent, id, descriptionOrigin ? correlation.DescriptionMarkerText : correlation.Tag, descriptionOrigin, creation.RootElement), current,
                authorizer, rationale, DateTimeOffset.UtcNow);
            var appended = await _registry.AppendConnectionRemoteWriteReceiptAsync(intent, receipt, ct).ConfigureAwait(false);
            if (!appended.IsSuccess) throw new InvalidOperationException(appended.Error);
            return descriptionOrigin
                ? await CleanupConfirmedSeedAsync(history with { Receipt = receipt }, current, provider, ct).ConfigureAwait(false)
                : Report(history with { Receipt = receipt }, []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return Report(history, ["remote-write-seed-readback-unavailable: " + ex.Message + " The original create uncertainty stays durable; no request was replayed."]); }
        finally { (provider as IDisposable)?.Dispose(); }
    }
    private async Task<ConnectionRemoteWriteInspection> CleanupConfirmedSeedAsync(ConnectionRemoteWriteHistory history,
        ResolvedConnectionBinding current, Twig.Domain.Interfaces.IAuthenticationProvider provider, CancellationToken ct)
    {
        try
        {
            var id = MigrationBoundAuthenticationProvider.PublishedIdFromReceipt(history);
            if (id is not { } publishedId || history.Intent.Request.SeedCorrelation is not { } correlation)
                return Report(history, []);
            var client = new Twig.Infrastructure.Ado.AdoRestClient(_http, provider,
                current.Operation.Organization, current.Operation.Project, new Twig.Domain.Services.WorkItemMapper());
            await client.ClearPublishMetadataAsync(publishedId, correlation, ct).ConfigureAwait(false);
            return Report(history, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Report(history, ["publish-metadata-cleanup-pending: native create is confirmed; temporary metadata could not be removed: " + ex.Message]);
        }
    }


    private static string SeedReceiptEvidence(Twig.Domain.ValueObjects.PublishIntent intent, int mappedId,
        string originMarker, bool descriptionOrigin, JsonElement creation)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("stagedIdentity", intent.Identity.ToString());
        writer.WriteString("intentRecordedAt", intent.RecordedAt);
        writer.WriteString("intentCompletedAt", intent.CompletedAt!.Value);
        writer.WriteNumber("publishedId", intent.PublishedId!.Value);
        writer.WriteNumber("mappedId", mappedId);
        writer.WriteString(descriptionOrigin ? "descriptionMarker" : "correlationTag", originMarker);
        writer.WritePropertyName("immutableCreationReadback");
        creation.WriteTo(writer);
        writer.WriteEndObject();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private async Task<JsonDocument> ReadAsync(Uri target, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        AdoErrorHandler.ApplyAuthHeader(request, token);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await AdoErrorHandler.ThrowOnErrorAsync(response, target.AbsoluteUri, ct, credential: token).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool TryRevisionFence(ConnectionRemoteWriteRequest request, out int revision)
    {
        revision = 0;
        if (request.Method != "PATCH" || request.EffectKind is not ("workitem-patch" or "link-add")
            || !int.TryParse(request.IfMatch, NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision <= 0
            || ConnectionRemoteWriteAdmission.TargetWorkItemId(request.Target) is null) return false;
        try
        {
            using var payload = JsonDocument.Parse(request.Payload);
            if (payload.RootElement.ValueKind != JsonValueKind.Array || payload.RootElement.GetArrayLength() < 2) return false;
            var first = payload.RootElement[0];
            return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("op", out var op)
                && op.ValueKind == JsonValueKind.String && op.GetString() == "test"
                && first.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String && path.GetString() == "/rev"
                && first.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var expected) && expected == revision;
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesExpectedEffects(string request, JsonElement snapshot, bool hasRevisionTest = true)
    {
        using var payload = JsonDocument.Parse(request);
        foreach (var operation in payload.RootElement.EnumerateArray().Skip(hasRevisionTest ? 1 : 0))
        {
            if (!operation.TryGetProperty("op", out var opValue) || !operation.TryGetProperty("path", out var pathValue)
                || opValue.ValueKind != JsonValueKind.String || pathValue.ValueKind != JsonValueKind.String) return false;
            var op = opValue.GetString();
            var path = pathValue.GetString()!;
            if (path.StartsWith("/fields/", StringComparison.Ordinal))
            {
                var field = path[8..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                var exists = snapshot.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(field, out _);
                if (op == "remove") { if (exists) return false; }
                else if (op is "add" or "replace")
                {
                    if (!exists || !operation.TryGetProperty("value", out var expected) || !JsonElement.DeepEquals(expected, fields.GetProperty(field))) return false;
                }
                else return false;
            }
            else if (op == "add" && path == "/relations/-" && operation.TryGetProperty("value", out var expected)
                && snapshot.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
            {
                if (!relations.EnumerateArray().Any(actual => ContainsExpected(expected, actual))) return false;
            }
            else return false;
        }
        return true;
    }

    private static bool ContainsExpected(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != JsonValueKind.Object) return JsonElement.DeepEquals(expected, actual);
        if (actual.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in expected.EnumerateObject())
            if (!actual.TryGetProperty(property.Name, out var observed) || !ContainsExpected(property.Value, observed)) return false;
        return true;
    }

    private static bool TryWorkItemRevision(JsonElement item, int? expectedId, out int revision)
    {
        revision = 0;
        return expectedId is { } id && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var observedId)
            && observedId.ValueKind == JsonValueKind.Number && observedId.TryGetInt32(out var actualId)
            && actualId == id && item.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.Number
            && rev.TryGetInt32(out revision) && revision > 0;
    }

    private static ConnectionRemoteWriteInspection Report(ConnectionRemoteWriteHistory history, IReadOnlyList<string> blockers)
    {
        var isSettled = history.Receipt is not null;
        var canReconcile = !isSettled && blockers.Count == 0 && (TryRevisionFence(history.Intent.Request, out _)
            || history.Intent.Request.EffectKind == "workitem-create" && history.Intent.Request.SeedCorrelation is not null);
        return new(isSettled ? "settled" : "unknown", history.Intent.IntentId, history.Intent.RequestDigest, canReconcile,
            history.Intent.Origin.WorktreeRoot, history.Intent.Origin.Binding.BindingId, history.Intent.Origin.Identity.IdentityId,
            history.Intent.Request, history.Observations, history.Receipt, blockers,
            isSettled ? ["Native immutable outcome receipt is preserved; this request is never replayed by inspection/reconciliation."]
                : canReconcile ? ["Explicitly reconcile this exact request digest using the original registered identity id and rationale. Native fresh immutable readback must prove exhausted-CAS effect or exact correlated completed publish-intent/ID-map creation evidence."]
                : ["Preserve original request/response evidence and recover through the original actor. Without attributable server outcome or exhausted-CAS proof this intent remains blocked; lease expiry, matching text/current state, pending discard and force are not evidence."]);
    }

    private static void Require<T>(Twig.Domain.Common.Result<T> result)
    { if (!result.IsSuccess) throw new InvalidOperationException(result.Error); }
    public void Dispose()
    {
        _registry.Dispose();
        if (_ownsHttp) _http.Dispose();
    }
}
