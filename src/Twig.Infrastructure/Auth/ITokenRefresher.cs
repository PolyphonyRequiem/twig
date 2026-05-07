namespace Twig.Infrastructure.Auth;

/// <summary>
/// Exchanges a refresh token for a fresh ADO-scoped access token via direct AAD HTTP.
/// Abstraction exists primarily so tests can swap in canned responses without
/// emulating the full token endpoint protocol.
/// </summary>
internal interface ITokenRefresher
{
    /// <summary>
    /// Attempts to refresh the access token. Returns (token, isInvalidGrant) where
    /// <c>isInvalidGrant=true</c> signals the refresh token has been revoked and the
    /// caller should drop any cached refresh token.
    /// </summary>
    Task<(string? AccessToken, bool IsInvalidGrant)> TryRefreshAsync(
        string refreshToken,
        string clientId,
        string tenantId,
        string authorityHost,
        CancellationToken ct = default);
}
