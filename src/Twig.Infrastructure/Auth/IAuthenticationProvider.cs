namespace Twig.Infrastructure.Auth;

/// <summary>
/// Provides authentication tokens for Azure DevOps API access.
/// Implemented by explicitly selected, identity-bound credential providers.
/// </summary>
public interface IAuthenticationProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears any cached token so the next <see cref="GetAccessTokenAsync"/> call
    /// acquires a fresh one. This does not select or adopt another identity.
    /// </summary>
    void InvalidateToken();
}
