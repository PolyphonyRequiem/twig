using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Config;

namespace Twig.Infrastructure.Auth;

/// <summary>Freezes publication authority from the admitted binding, never from caller labels.</summary>
internal sealed class BoundPlanOriginProvider(
    IConnectionBindingService bindings,
    TwigConfiguration configuration,
    TwigPaths paths,
    IAuthenticationProvider authentication) : IPlanOriginProvider
{
    public async Task<PlanOrigin> GetOriginAsync(CancellationToken ct = default)
    {
        var currentConfiguration = await TwigConfiguration.LoadSplitAsync(paths, ct).ConfigureAwait(false);
        if (!string.Equals(currentConfiguration.Organization, configuration.Organization, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentConfiguration.Project, configuration.Project, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repository endpoint changed. Reconnect explicitly before publication.");
        var selected = await bindings.ResolveAsync(currentConfiguration, paths, ct).ConfigureAwait(false);
        _ = await authentication.GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (authentication is not IBoundAuthenticationMetadata metadata)
            throw new InvalidOperationException("Publication origin is unknown: an attached bound runtime is required.");
        var admitted = await metadata.GetBoundIdentityAsync(ct).ConfigureAwait(false);
        if (!SamePrincipal(admitted, selected.Identity))
            throw new InvalidOperationException("Publication binding changed. Reconnect explicitly before creating or resuming proposals.");
        var identity = selected.Identity;
        return new PlanOrigin(
            selected.WorktreeRoot, selected.Operation.WorktreeFingerprint,
            selected.Operation.AttachmentRevision, selected.Binding.ConnectionRef,
            selected.Binding.BindingId, selected.Binding.Revision, selected.SelectionSource,
            selected.SelectionRevision, identity.IdentityId, identity.Method, identity.CredentialRef,
            identity.TenantId, identity.ObjectId, identity.Issuer,
            identity.Method == "pat" ? identity.AdoAuthority! : identity.AuthorityHost,
            identity.AdoPrincipalId);
    }

    private static bool SamePrincipal(AuthenticationIdentity admitted, AuthenticationIdentity selected)
        => admitted.IdentityId == selected.IdentityId && admitted.Method == selected.Method
            && admitted.CredentialRef == selected.CredentialRef && admitted.TenantId == selected.TenantId
            && admitted.ObjectId == selected.ObjectId && admitted.Issuer == selected.Issuer
            && admitted.AuthorityHost == selected.AuthorityHost && admitted.AdoAuthority == selected.AdoAuthority
            && admitted.AdoPrincipalId == selected.AdoPrincipalId;
}
