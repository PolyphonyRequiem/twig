namespace Twig.Domain.Interfaces;

/// <summary>
/// Provides authentication tokens for Azure DevOps API access.
/// Implemented in Infrastructure (AzCli / PAT).
/// </summary>
public interface IAuthenticationProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears any cached token so the next <see cref="GetAccessTokenAsync"/> call
    /// acquires a fresh one. Called by the HTTP layer on auth failures (401 / 203 HTML challenge)
    /// before retrying the request.
    /// </summary>
    void InvalidateToken();
}
