using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Auth;

/// <summary>Registered principal metadata. Credential material is stored separately.</summary>
internal sealed record AuthenticationIdentity(
    string IdentityId,
    string Name,
    string TenantId,
    string ObjectId,
    string Issuer,
    string AuthorityHost,
    string CredentialRef,
    string? AccountName,
    string Method = "aad",
    string? AdoPrincipalId = null,
    string? AdoAuthority = null);

/// <summary>An explicit association between an endpoint and a registered identity.</summary>
internal sealed record IdentityBinding(
    string BindingId,
    string ConnectionRef,
    string IdentityId,
    long Revision);

/// <summary>The immutable identity selection admitted for one attached checkout.</summary>
internal sealed record ResolvedConnectionBinding(
    IdentityBinding Binding,
    AuthenticationIdentity Identity,
    string WorktreeRoot,
    string SelectionSource,
    long SelectionRevision,
    ConnectionOperationSnapshot Operation,
    string? StorageGeneration = null);

/// <summary>Frozen endpoint and attachment CAS evidence for operation admission.</summary>
internal sealed record ConnectionOperationSnapshot(
    string Organization,
    string Project,
    string Team,
    string WorktreeFingerprint,
    long AttachmentRevision,
    string PortableConfigurationSource,
    string UserPreferencesSource,
    string DisplayIcons,
    string DisplayIconsSource);

/// <summary>Frozen principal evidence from an admitted provider, never a new selector.</summary>
internal interface IBoundAuthenticationMetadata
{
    Task<AuthenticationIdentity> GetBoundIdentityAsync(CancellationToken ct = default);
}

/// <summary>Holds checkout authority for the complete consumer operation, not only token acquisition.</summary>
internal interface IConnectionOperationGuard
{
    Task<IDisposable> AcquireOperationAsync(CancellationToken ct = default);
}

internal sealed record ConnectionRemoteWriteRequest(string EffectKind, string Method, string Target, string Payload, string? IfMatch,
    SeedPublishCorrelation? SeedCorrelation = null);

internal sealed record ConnectionRemoteWriteResponse(int StatusCode, string Body, string? RequestId, string? Location);

internal interface IConnectionRemoteWriteGuard
{
    Task<IConnectionRemoteWriteAdmission> BeginRemoteWriteAsync(ConnectionRemoteWriteRequest request, CancellationToken ct = default);
}

internal interface IConnectionRemoteWriteAdmission
{
    bool IsDefinitivelyRejected { get; }
    Task RecordResponseAsync(ConnectionRemoteWriteResponse response, CancellationToken ct = default);
}


internal static class ConnectionOperationAdmission
{
    private static readonly object RejectedWriteAdmissionKey = new();
    internal static Task<IDisposable?> AcquireAsync(IAuthenticationProvider provider, CancellationToken ct)
        => provider is IConnectionOperationGuard guard
            ? AsNullableAsync(guard.AcquireOperationAsync(ct)) : Task.FromResult<IDisposable?>(null);

    private static async Task<IDisposable?> AsNullableAsync(Task<IDisposable> operation)
        => await operation.ConfigureAwait(false);

    internal static async Task<IConnectionRemoteWriteAdmission?> BeginRemoteWriteAsync(
        IAuthenticationProvider provider, HttpRequestMessage request, string? effectKind, CancellationToken ct,
        SeedPublishCorrelation? seedCorrelation = null)
    {
        if (effectKind is null) return null;
        if (provider is not IConnectionRemoteWriteGuard guard)
            throw new InvalidOperationException("remote-write-admission-required: normal writes require an attached bound runtime with native outcome admission; reconnect explicitly before publication.");
        var payload = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.FirstOrDefault() : null;
        return await guard.BeginRemoteWriteAsync(new ConnectionRemoteWriteRequest(effectKind, request.Method.Method,
            request.RequestUri!.AbsoluteUri, payload, ifMatch, seedCorrelation), ct).ConfigureAwait(false);
    }

    internal static async Task RecordRemoteWriteResponseAsync(
        IConnectionRemoteWriteAdmission? admission, HttpResponseMessage response, string credential, string? authorizationParameter, CancellationToken ct)
    {
        if (admission is null) return;
        var body = RedactCredential(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), credential, authorizationParameter);
        var requestId = response.Headers.TryGetValues("X-VSS-E2EID", out var values) ? values.FirstOrDefault()
            : response.Headers.TryGetValues("X-MS-Correlation-Request-Id", out values) ? values.FirstOrDefault() : null;
        await admission.RecordResponseAsync(new ConnectionRemoteWriteResponse((int)response.StatusCode, body,
            requestId is null ? null : RedactCredential(requestId, credential, authorizationParameter),
            response.Headers.Location is { } location ? RedactCredential(location.ToString(), credential, authorizationParameter) : null), ct).ConfigureAwait(false);
    }

    internal static void RecordRejectedWriteAuthException(Exception exception, IConnectionRemoteWriteAdmission? admission)
    {
        if (admission?.IsDefinitivelyRejected == true) exception.Data[RejectedWriteAdmissionKey] = admission;
    }

    internal static bool CanRenewAfterWriteRejection(Exception exception)
        => exception.Data[RejectedWriteAdmissionKey] is IConnectionRemoteWriteAdmission { IsDefinitivelyRejected: true };

    private static string RedactCredential(string value, string credential, string? authorizationParameter)
    {
        value = Redact(value, credential);
        if (!string.IsNullOrEmpty(authorizationParameter)) value = Redact(value, authorizationParameter);
        if (credential.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(credential[6..]));
                var separator = decoded.IndexOf(':');
                if (separator >= 0) value = Redact(value, decoded[(separator + 1)..]);
            }
            catch (FormatException) { }
        }
        return value;
    }

    private static string Redact(string value, string secret)
        => string.IsNullOrEmpty(secret) ? value : value.Replace(secret, "[redacted]", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal)
            .Replace(System.Net.WebUtility.HtmlEncode(secret), "[redacted]", StringComparison.Ordinal)
            .Replace(System.Text.Json.JsonEncodedText.Encode(secret).ToString(), "[redacted]", StringComparison.Ordinal);
}
internal static class ConnectionOperationGate
{
    private static readonly AsyncLocal<string?> ExclusiveRoot = new();

    internal static IDisposable Acquire(string worktreeRoot, bool exclusive = false)
    {
        var root = Path.GetFullPath(worktreeRoot);
        if (SqlitePathComparer.Equals(root, ExclusiveRoot.Value)) return EmptyLease.Instance;
        var directory = Path.Combine(root, ".twig");
        Directory.CreateDirectory(directory);
        FileStream stream;
        try
        {
            stream = new FileStream(Path.Combine(directory, "binding-operation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("binding-operation-active: a checkout read, cache fill, write or native transition is still executing. Wait for it to settle, then retry; uncertain writes require native reconciliation.", ex);
        }
        var prior = ExclusiveRoot.Value;
        if (exclusive) ExclusiveRoot.Value = root;
        return new Lease(stream, exclusive, prior);
    }

    private static StringComparer SqlitePathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Lease(FileStream stream, bool exclusive, string? prior) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            stream.Dispose();
            if (exclusive) ExclusiveRoot.Value = prior;
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Shared enrollment, initial binding and resolution seam. Initial-default setup is
/// permitted only when no different default already exists; identity transitions
/// are deliberate guarded management operations, not a login side effect.
/// </summary>
internal interface IConnectionBindingService
{
    string RegistryPath { get; }

    Task<AuthenticationIdentity> RegisterAadIdentityAsync(
        string name,
        TwigRefreshTokenStoreEntry credential,
        CancellationToken ct = default);

    Task<AuthenticationIdentity> RegisterPatIdentityAsync(
        string name,
        string organization,
        string pat,
        CancellationToken ct = default);

    Task ClearIdentityAccessCacheAsync(string name, CancellationToken ct = default);

    Task VerifyIdentityCredentialAsync(string identityName, string organization, CancellationToken ct = default);

    Task<IReadOnlyList<AuthenticationIdentity>> ListIdentitiesAsync(CancellationToken ct = default);

    Task<IdentityBinding> CreateBindingAsync(
        string organization,
        string project,
        string identityName,
        bool makeDefault,
        CancellationToken ct = default);

    Task<IReadOnlyList<IdentityBinding>> ListBindingsAsync(
        string organization,
        string project,
        CancellationToken ct = default);

    Task<ResolvedConnectionBinding> ResolveAsync(
        TwigConfiguration configuration,
        TwigPaths paths,
        CancellationToken ct = default);


    Task<IAuthenticationProvider> CreateBootstrapProviderAsync(
        TwigConfiguration configuration,
        CancellationToken ct = default);
    IAuthenticationProvider CreateAuthenticationProvider(ResolvedConnectionBinding binding);
}
