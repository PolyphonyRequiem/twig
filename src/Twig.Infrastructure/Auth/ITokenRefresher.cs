namespace Twig.Infrastructure.Auth;

/// <summary>
/// Exchanges a refresh token for a fresh ADO-scoped access token via direct AAD HTTP.
/// Abstraction exists primarily so tests can swap in canned responses without
/// emulating the full token endpoint protocol.
/// </summary>
internal interface ITokenRefresher
{
    /// <summary>
    /// Attempts to refresh the access token. Returns the new access token, the rotated
    /// refresh token (if AAD issued one — usually does), and an invalid-grant flag.
    /// <para>
    /// <c>isInvalidGrant=true</c> signals the refresh token has been revoked and the caller
    /// should drop any cached refresh token. <c>RefreshToken</c> may be null even on success
    /// if the server reused the existing RT — callers should keep their existing RT in that case.
    /// </para>
    /// </summary>
    Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
        string refreshToken,
        string clientId,
        string tenantId,
        string authorityHost,
        CancellationToken ct = default);
}
