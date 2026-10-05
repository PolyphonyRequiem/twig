using Twig.Domain.Interfaces;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Ado;

/// <summary>Server budget identity, shared by aliases of the same admitted principal.</summary>
internal readonly record struct AdoRateLimitBudget(string Authority, string Principal)
{
    internal static async Task<AdoRateLimitBudget?> FromProviderAsync(
        IAuthenticationProvider provider, string orgUrl, CancellationToken ct)
    {
        // Unbound adapters used outside the attached runtime cannot invent principal
        // evidence. They share concurrency only, never an anonymous global pause.
        if (provider is not IBoundAuthenticationMetadata metadata)
            return null;
        var identity = await metadata.GetBoundIdentityAsync(ct).ConfigureAwait(false);
        var endpoint = AdoRestClient.NormalizeOrgUrl(orgUrl);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var target)
            || target.Scheme is not ("https" or "http") || target.UserInfo.Length != 0
            || target.Query.Length != 0 || target.Fragment.Length != 0)
            throw new InvalidOperationException("The ADO request authority is invalid for budget accounting.");
        var authority = target.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();
        string principal;
        if (identity.Method == "pat")
        {
            if (string.IsNullOrWhiteSpace(identity.AdoPrincipalId)
                || string.IsNullOrWhiteSpace(identity.AdoAuthority)
                || !string.Equals(authority, PatPrincipalAttestor.NormalizeAuthority(identity.AdoAuthority), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Bound PAT principal evidence does not match the request authority.");
            principal = "pat:" + identity.AdoPrincipalId.ToLowerInvariant();
        }
        else if (identity.Method == "aad" && !string.IsNullOrWhiteSpace(identity.Issuer)
            && !string.IsNullOrWhiteSpace(identity.TenantId) && !string.IsNullOrWhiteSpace(identity.ObjectId))
        {
            principal = "aad:" + identity.Issuer.TrimEnd('/').ToLowerInvariant()
                + ":" + identity.TenantId.ToLowerInvariant() + ":" + identity.ObjectId.ToLowerInvariant();
        }
        else
        {
            throw new InvalidOperationException("Bound authentication provider has incomplete principal evidence.");
        }
        return new AdoRateLimitBudget(authority, principal);
    }
}
