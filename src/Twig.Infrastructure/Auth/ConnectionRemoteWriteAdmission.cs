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
        ValidateRequest(binding, request);
        await ValidateSeedCorrelationAsync(registryPath, binding, request, ct).ConfigureAwait(false);
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

    internal static string RequestDigest(ResolvedConnectionBinding origin, ConnectionRemoteWriteRequest request)
    {
        // Duplicate/replay identity excludes labels, preferences and unrelated scope revisions.
        var evidence = string.Join('\n', origin.Binding.ConnectionRef, origin.Identity.IdentityId,
            origin.Identity.Method, origin.Identity.CredentialRef, request.EffectKind, request.Method,
            request.Target, request.IfMatch, request.Payload);
        if (request.SeedCorrelation is { } seed)
            evidence += "\nseed\n" + seed.Identity;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
    }

    private static async Task ValidateSeedCorrelationAsync(string registryPath, ResolvedConnectionBinding binding,
        ConnectionRemoteWriteRequest request, CancellationToken ct)
    {
        if (request.SeedCorrelation is not { } correlation) return;
        if (request.EffectKind != "workitem-create" || request.Method != "POST")
            throw new InvalidOperationException("remote-write-seed-correlation-invalid: native staged create metadata cannot authorize another mutation kind.");
        var twigDir = Path.Combine(binding.WorktreeRoot, ".twig");
        var configuration = await Twig.Infrastructure.Config.TwigConfiguration.LoadSplitAsync(
            Twig.Infrastructure.Config.TwigPaths.BuildPaths(twigDir, new Twig.Infrastructure.Config.TwigConfiguration
            { Organization = binding.Operation.Organization, Project = binding.Operation.Project, Team = binding.Operation.Team }, binding.WorktreeRoot), ct).ConfigureAwait(false);
        var paths = Twig.Infrastructure.Config.TwigPaths.BuildPaths(twigDir, configuration, binding.WorktreeRoot);
        using var store = SqliteCacheStore.OpenWorkspace(paths, registryPath);
        var intent = await new SqlitePublishIntentRepository(store).GetIntentAsync(correlation.Identity, ct).ConfigureAwait(false);
        if (intent is null || intent.RecordedAt != correlation.IntentRecordedAt || intent.PublishedId is not null
            || SeedCreateTitle(request.Payload) != intent.Title || SeedCreateType(request.Target) != intent.TypeName
            || !SeedCreateHasCorrelation(request.Payload, correlation.Tag))
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
            "workitem-patch" or "link-add" or "link-remove" => "PATCH",
            "workitem-create" or "comment-add" or "git-pr-create" => "POST",
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
