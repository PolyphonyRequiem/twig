using Twig.Domain.Interfaces;
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
    ConnectionOperationSnapshot Operation);

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

/// <summary>
/// Shared enrollment, initial binding and resolution seam. Initial-default setup is
/// permitted only when no different default already exists; identity transitions
/// are deliberate guarded management operations, not a login side effect.
/// </summary>
internal interface IConnectionBindingService
{
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
