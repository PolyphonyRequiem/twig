using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>A native request is admitted before HTTP. Losing a process or response never settles its outcome.</summary>
internal sealed class ConnectionRemoteWriteAdmission : IConnectionRemoteWriteAdmission
{
    private readonly string _registryPath;
    private readonly ConnectionRemoteWriteIntent _intent;

    public bool IsDefinitivelyRejected { get; private set; }

    private ConnectionRemoteWriteAdmission(string registryPath, ConnectionRemoteWriteIntent intent)
    {
        _registryPath = registryPath;
        _intent = intent;
    }

    internal static async Task<IConnectionRemoteWriteAdmission> BeginAsync(string registryPath,
        ResolvedConnectionBinding binding, ConnectionRemoteWriteRequest request, CancellationToken ct = default)
    {
        return await BeginCoreAsync(registryPath, binding, request, null, ct).ConfigureAwait(false);
    }

    internal static Task<IConnectionRemoteWriteAdmission> BeginWithWorkspaceStoreAsync(string registryPath,
        ResolvedConnectionBinding binding, ConnectionRemoteWriteRequest request, SqliteCacheStore workspaceStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceStore);
        return BeginCoreAsync(registryPath, binding, request, workspaceStore, ct);
    }

    private static async Task<IConnectionRemoteWriteAdmission> BeginCoreAsync(string registryPath,
        ResolvedConnectionBinding binding, ConnectionRemoteWriteRequest request, SqliteCacheStore? workspaceStore,
        CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(binding.WorktreeRoot);
        ValidateRequest(binding, request);
        if (request.SeedCorrelation is not null
            && (request.EffectKind != "workitem-create" || request.Method != "POST"))
            throw new InvalidOperationException("remote-write-seed-correlation-invalid: native staged create metadata cannot authorize another mutation kind.");
        if (workspaceStore is null)
            await ValidateSeedCorrelationAsync(registryPath, binding, request, ct).ConfigureAwait(false);
        else
            await ValidateSeedCorrelationAsync(workspaceStore, request, ct).ConfigureAwait(false);
        var intent = new ConnectionRemoteWriteIntent(1, Guid.NewGuid().ToString("N"), RequestDigest(binding, request),
            binding.Operation.WorktreeFingerprint, binding, request, DateTimeOffset.UtcNow);
        using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        var admitted = await registry.BeginConnectionRemoteWriteAsync(intent, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) throw new InvalidOperationException(admitted.Error);
        return new ConnectionRemoteWriteAdmission(registryPath, intent);
    }

    public async Task RecordResponseAsync(ConnectionRemoteWriteResponse response, CancellationToken ct = default)
    {
        var observation = new ConnectionRemoteWriteObservation(1, Guid.NewGuid().ToString("N"), _intent.IntentId,
            _intent.RequestDigest, response, DateTimeOffset.UtcNow);
        var kind = ClassifyAcknowledgement(_intent.Request, response);
        var receipt = kind is null ? null : new ConnectionRemoteWriteReceipt(1, Guid.NewGuid().ToString("N"),
            _intent.IntentId, _intent.RequestDigest, kind, observation.ObservationId,
            JsonSerializer.Serialize(response, TwigJsonContext.Default.ConnectionRemoteWriteResponse), _intent.Origin,
            null, null, DateTimeOffset.UtcNow);
        using var registry = new SqliteSystemWorktreeRegistry(_registryPath, TimeProvider.System);
        var recorded = await registry.ObserveConnectionRemoteWriteAsync(_intent, observation, receipt, ct).ConfigureAwait(false);
        if (!recorded.IsSuccess) throw new InvalidOperationException(recorded.Error);
        IsDefinitivelyRejected = kind == "rejected";
    }

    public async Task RecordNotDispatchedAsync(CancellationToken ct = default)
    {
        var response = new ConnectionRemoteWriteResponse(0, "not-dispatched", null, null);
        var observation = new ConnectionRemoteWriteObservation(1, Guid.NewGuid().ToString("N"), _intent.IntentId,
            _intent.RequestDigest, response, DateTimeOffset.UtcNow);
        var receipt = new ConnectionRemoteWriteReceipt(1, Guid.NewGuid().ToString("N"), _intent.IntentId,
            _intent.RequestDigest, "not-dispatched", observation.ObservationId,
            JsonSerializer.Serialize(response, TwigJsonContext.Default.ConnectionRemoteWriteResponse), _intent.Origin,
            null, null, DateTimeOffset.UtcNow);
        using var registry = new SqliteSystemWorktreeRegistry(_registryPath, TimeProvider.System);
        var recorded = await registry.ObserveConnectionRemoteWriteAsync(_intent, observation, receipt, ct).ConfigureAwait(false);
        if (!recorded.IsSuccess) throw new InvalidOperationException(recorded.Error);
    }

    internal static string RequestDigest(ResolvedConnectionBinding origin, ConnectionRemoteWriteRequest request)
    {
        // Duplicate/replay identity excludes labels, preferences and unrelated scope revisions.
        var evidence = string.Join('\n', origin.Binding.ConnectionRef, origin.Identity.IdentityId,
            origin.Identity.Method, origin.Identity.CredentialRef, request.EffectKind, request.Method,
            request.Target, request.IfMatch, request.Payload);
        if (request.Evidence is not null)
            evidence += "\n" + request.Evidence;
        if (request.SeedCorrelation is { } seed)
            evidence += "\nseed\n" + seed.Identity;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }

    private static async Task ValidateSeedCorrelationAsync(string registryPath, ResolvedConnectionBinding binding,
        ConnectionRemoteWriteRequest request, CancellationToken ct)
    {
        if (request.SeedCorrelation is not { } correlation) return;
        var twigDir = Path.Combine(binding.WorktreeRoot, ".twig");
        var configuration = await Twig.Infrastructure.Config.TwigConfiguration.LoadSplitAsync(
            Twig.Infrastructure.Config.TwigPaths.BuildPaths(twigDir, new Twig.Infrastructure.Config.TwigConfiguration
            { Organization = binding.Operation.Organization, Project = binding.Operation.Project, Team = binding.Operation.Team }, binding.WorktreeRoot), ct).ConfigureAwait(false);
        var paths = Twig.Infrastructure.Config.TwigPaths.BuildPaths(twigDir, configuration, binding.WorktreeRoot);
        using var store = SqliteCacheStore.OpenWorkspace(paths, registryPath);
        await ValidateSeedCorrelationAsync(store, request, ct).ConfigureAwait(false);
    }

    private static async Task ValidateSeedCorrelationAsync(
        SqliteCacheStore store, ConnectionRemoteWriteRequest request, CancellationToken ct)
    {
        if (request.SeedCorrelation is not { } correlation) return;
        var intent = await new SqlitePublishIntentRepository(store).GetIntentAsync(correlation.Identity, ct).ConfigureAwait(false);
        if (intent is not null && intent.RecordedAt == correlation.IntentRecordedAt
            && new SqlitePublishIntentRepository(store).HasValidatedRejectedCreateFamilyDisposition(intent))
            throw new InvalidOperationException(
                "remote-write-seed-correlation-settled: this staged identity was already settled as a rejected create family. Use a new explicitly authorized identity; no HTTP mutation was admitted.");
        if (intent is null || intent.RecordedAt != correlation.IntentRecordedAt || intent.PublishedId is not null
            || SeedCreateTitle(request.Payload) != intent.Title || SeedCreateType(request.Target) != intent.TypeName
            || !SeedCreateHasDescriptionCorrelation(request.Payload, correlation))
            throw new InvalidOperationException("remote-write-seed-correlation-mismatch: the original open durable publish intent does not match this exact staged create request. No HTTP mutation was admitted.");
    }

    internal static string? SeedCreateTitle(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var op in json.RootElement.EnumerateArray())
            if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String && path.GetString() == "/fields/System.Title"
                && op.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    internal static bool SeedCreateHasCorrelation(string payload, string tag)
    {
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
        foreach (var op in json.RootElement.EnumerateArray())
            if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String && path.GetString() == "/fields/System.Tags"
                && op.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                return (value.GetString() ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains(tag, StringComparer.Ordinal);
        return false;
    }

    internal static bool SeedCreateHasDescriptionCorrelation(string payload, Twig.Domain.ValueObjects.SeedPublishCorrelation correlation)
    {
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Array) return false;
        foreach (var op in json.RootElement.EnumerateArray())
            if (op.ValueKind == JsonValueKind.Object && op.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String && string.Equals(path.GetString(), "/fields/System.Description", StringComparison.OrdinalIgnoreCase)
                && op.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                return correlation.ContainsDescriptionMarker(value.GetString());
        return false;
    }

    internal static string? SeedCreateType(string target)
    {
        var uri = new Uri(target);
        var tail = Uri.UnescapeDataString(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last());
        return tail.StartsWith('$') ? tail[1..] : null;
    }

    internal static bool SameAuthority(ResolvedConnectionBinding current, ResolvedConnectionBinding original)
        => current.Binding == original.Binding && current.Operation.WorktreeFingerprint == original.Operation.WorktreeFingerprint
            && current.SelectionSource == original.SelectionSource && current.StorageGeneration == original.StorageGeneration
            && (current.SelectionSource == "checkout-binding-pin" || current.SelectionRevision == original.SelectionRevision)
            && current.Identity.IdentityId == original.Identity.IdentityId && current.Identity.Method == original.Identity.Method
            && current.Identity.CredentialRef == original.Identity.CredentialRef && current.Identity.TenantId == original.Identity.TenantId
            && current.Identity.ObjectId == original.Identity.ObjectId && current.Identity.Issuer == original.Identity.Issuer
            && current.Identity.AuthorityHost == original.Identity.AuthorityHost && current.Identity.AdoPrincipalId == original.Identity.AdoPrincipalId
            && current.Identity.AdoAuthority == original.Identity.AdoAuthority;

    private static void ValidateRequest(ResolvedConnectionBinding binding, ConnectionRemoteWriteRequest request)
    {
        var method = request.EffectKind switch
        {
            "workitem-patch" or "link-add" or "link-remove" or "attachment-link" => "PATCH",
            "workitem-create" or "comment-add" or "git-pr-create" or "attachment-upload" => "POST",
            "workitem-delete" => "DELETE",
            _ => throw new InvalidOperationException("remote-write-kind-unsupported: explicit native mutation semantics are required.")
        };
        if (request.Method != method || !Uri.TryCreate(request.Target, UriKind.Absolute, out var target)
            || target.Scheme is not ("https" or "http") || target.UserInfo.Length != 0 || target.Fragment.Length != 0
            || !IsWithinAuthority(binding, target))
            throw new InvalidOperationException("remote-write-request-invalid: mutation method/target does not match explicit native semantics.");
    }

    internal static bool IsWithinAuthority(ResolvedConnectionBinding binding, Uri target)
    {
        var authority = new Uri(Twig.Infrastructure.Ado.AdoRestClient.NormalizeOrgUrl(binding.Operation.Organization));
        var prefix = authority.AbsolutePath.TrimEnd('/') + "/";
        return target.Scheme.Equals(authority.Scheme, StringComparison.OrdinalIgnoreCase)
            && target.Host.Equals(authority.Host, StringComparison.OrdinalIgnoreCase) && target.Port == authority.Port
            && target.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ClassifyAcknowledgement(ConnectionRemoteWriteRequest request, ConnectionRemoteWriteResponse response)
    {
        // Complete, definitive ADO rejection is known no-effect. Timeout/5xx/accepted-async is not.
        if (response.StatusCode is 400 or 401 or 403 or 404 or 405 or 409 or 412 or 415 or 422 or 429)
            return "rejected";
        if (response.StatusCode is not (200 or 201 or 204)) return null;
        if (request.EffectKind == "workitem-delete" && response.StatusCode == 204 && string.IsNullOrWhiteSpace(response.Body))
            return "acknowledged";
        try
        {
            using var body = JsonDocument.Parse(response.Body);
            var root = body.RootElement;
            if (request.EffectKind == "git-pr-create")
                return PositiveInt(root, "pullRequestId", out _) ? "acknowledged" : null;
            if (request.EffectKind == "attachment-upload")
                return AttachmentUploadReceiptMatches(request.Target, root) ? "acknowledged" : null;
            if (request.EffectKind == "attachment-link")
                return AttachmentLinkReceiptMatches(request, response) ? "acknowledged" : null;
            if (!PositiveInt(root, "id", out var id)) return null;
            if (request.EffectKind == "workitem-delete")
                return id == TargetWorkItemId(request.Target) ? "acknowledged" : null;
            if (request.EffectKind == "comment-add")
            {
                using var expected = JsonDocument.Parse(request.Payload);
                return expected.RootElement.TryGetProperty("text", out var text) && root.TryGetProperty("text", out var observed)
                    && text.ValueKind == JsonValueKind.String && observed.ValueKind == JsonValueKind.String
                    && text.GetString() == observed.GetString() ? "acknowledged" : null;
            }
            if (!PositiveInt(root, "rev", out var revision)) return null;
            if (request.EffectKind != "workitem-create" && TargetWorkItemId(request.Target) != id) return null;
            if (request.IfMatch is not null && (!int.TryParse(request.IfMatch, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRevision)
                || revision <= expectedRevision)) return null;
            return "acknowledged";
        }
        catch (JsonException) { return null; }
    }

    internal static bool AttachmentUploadReceiptMatches(string target, JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(idValue.GetString(), "D", out var id) || id == Guid.Empty
            || !root.TryGetProperty("url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(target, UriKind.Absolute, out var uploadTarget)
            || !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var attachmentUrl))
            return false;
        var apiIndex = uploadTarget.AbsolutePath.IndexOf("/_apis/", StringComparison.OrdinalIgnoreCase);
        if (apiIndex < 0) return false;
        var expectedPath = uploadTarget.AbsolutePath[..apiIndex] + "/_apis/wit/attachments/" + id.ToString("D");
        return attachmentUrl.Scheme.Equals(uploadTarget.Scheme, StringComparison.OrdinalIgnoreCase)
            && attachmentUrl.Host.Equals(uploadTarget.Host, StringComparison.OrdinalIgnoreCase)
            && attachmentUrl.Port == uploadTarget.Port
            && attachmentUrl.UserInfo.Length == 0 && attachmentUrl.Fragment.Length == 0
            && attachmentUrl.Query.Length == 0
            && attachmentUrl.AbsolutePath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool AttachmentLinkReceiptMatches(
        ConnectionRemoteWriteRequest request, ConnectionRemoteWriteResponse response)
    {
        var evidence = ReadAttachmentEvidence(request.Evidence ?? request.Payload);
        if (evidence?.AttachmentId is not { } attachmentId || evidence.AttachmentUrl is not { } attachmentUrl
            || !Guid.TryParseExact(attachmentId, "D", out var parsedAttachmentId) || parsedAttachmentId == Guid.Empty
            || TargetWorkItemId(request.Target) != evidence.WorkItemId
            || !int.TryParse(request.IfMatch, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedRevision)
            || expectedRevision != evidence.ExpectedRevision)
            return false;
        try
        {
            using var body = JsonDocument.Parse(response.Body);
            var root = body.RootElement;
            if (!PositiveInt(root, "id", out var id) || id != evidence.WorkItemId
                || !PositiveInt(root, "rev", out var revision) || revision <= evidence.ExpectedRevision
                || !root.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
                return false;
            return relations.EnumerateArray().Any(relation =>
                relation.ValueKind == JsonValueKind.Object
                && relation.TryGetProperty("rel", out var kind) && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "AttachedFile"
                && relation.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var observedUrl)
                && Uri.TryCreate(attachmentUrl, UriKind.Absolute, out var expectedUrl)
                && observedUrl.Scheme.Equals(expectedUrl.Scheme, StringComparison.OrdinalIgnoreCase)
                && observedUrl.Host.Equals(expectedUrl.Host, StringComparison.OrdinalIgnoreCase)
                && observedUrl.Port == expectedUrl.Port && observedUrl.UserInfo.Length == 0
                && observedUrl.Query.Length == 0 && observedUrl.Fragment.Length == 0
                && observedUrl.AbsolutePath.Equals(expectedUrl.AbsolutePath, StringComparison.OrdinalIgnoreCase)
                && expectedUrl.AbsolutePath.EndsWith("/" + parsedAttachmentId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return false; }
    }

    private static AttachmentRemoteWriteEvidence? ReadAttachmentEvidence(string payload)
    {
        try { return JsonSerializer.Deserialize(payload, TwigJsonContext.Default.AttachmentRemoteWriteEvidence); }
        catch (JsonException) { return null; }
    }

    internal static int? TargetWorkItemId(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
            if (segments[i].Equals("workitems", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segments[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                return id;
        return null;
    }

    private static bool PositiveInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value) && value > 0;
    }
}
