using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Performs direct HTTP refresh for an explicitly selected identity credential.
/// </summary>
internal sealed class MsalTokenRefresher : ITokenRefresher
{
    private const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpMessageHandler _handler;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a refresher with the default <see cref="HttpClientHandler"/>.
    /// </summary>
    public MsalTokenRefresher()
        : this(new HttpClientHandler(), null)
    {
    }

    /// <summary>
    /// Creates a refresher with an injectable handler and optional timeout (for testing).
    /// </summary>
    internal MsalTokenRefresher(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? DefaultRefreshTimeout;
    }

    /// <summary>
    /// Attempts to refresh the access token via direct HTTP POST to Azure AD.
    /// Returns (accessToken, rotatedRefreshToken, isInvalidGrant). Never throws (except
    /// caller-cancellation) — all errors are treated as "fall through to next provider".
    /// </summary>
    /// <param name="refreshToken">The refresh token secret.</param>
    /// <param name="clientId">The client ID the refresh token is bound to.</param>
    /// <param name="tenantId">The tenant/realm.</param>
    /// <param name="authorityHost">The authority host (e.g., login.microsoftonline.com).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> ITokenRefresher.TryRefreshAsync(
        string refreshToken,
        string clientId,
        string tenantId,
        string authorityHost,
        CancellationToken ct)
        => TryRefreshAsync(refreshToken, clientId, tenantId, authorityHost, ct);

    internal async Task<(string? AccessToken, string? RefreshToken, bool IsInvalidGrant)> TryRefreshAsync(
        string refreshToken,
        string clientId,
        string tenantId,
        string authorityHost,
        CancellationToken ct = default)
    {
        try
        {
            using var httpClient = new HttpClient(_handler, disposeHandler: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var tokenEndpoint = string.Create(CultureInfo.InvariantCulture,
                $"https://{authorityHost}/{tenantId}/oauth2/v2.0/token");

            // offline_access is required for AAD to return a (rotated) refresh_token in the response.
            // Without it, even though the request itself is a refresh_token grant, the server may
            // omit refresh_token from the response and our stored RT slowly ages out.
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = AdoScope + " offline_access",
            });

            using var response = await httpClient.PostAsync(tokenEndpoint, content, timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var tokenResponse = JsonSerializer.Deserialize(responseBody, TwigJsonContext.Default.TokenRefreshResponse);

            if (tokenResponse?.AccessToken is { Length: > 0 } accessToken)
            {
                // Rotated RT may be null (server reused existing) — caller handles that.
                var rotatedRt = tokenResponse.RefreshToken is { Length: > 0 } rt ? rt : null;
                return (accessToken, rotatedRt, false);
            }

            var isInvalidGrant = string.Equals(tokenResponse?.Error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
            return (null, null, isInvalidGrant);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our internal timeout fired — not the caller's cancellation
            return (null, null, false);
        }
        catch (OperationCanceledException)
        {
            // Caller's cancellation — propagate
            throw;
        }
        catch
        {
            // Network error, DNS failure, JSON parse error, etc. — fall through
            return (null, null, false);
        }
    }

}
