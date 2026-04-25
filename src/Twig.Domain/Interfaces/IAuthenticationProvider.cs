namespace Twig.Domain.Interfaces;

/// <summary>
/// Provides authentication tokens for Azure DevOps API access.
/// Implemented in Infrastructure (AzCli / PAT).
/// </summary>
public interface IAuthenticationProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}
