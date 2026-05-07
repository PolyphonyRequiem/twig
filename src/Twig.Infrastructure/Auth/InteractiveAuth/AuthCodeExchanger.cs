using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// Exchanges an authorization code (from the loopback redirect or device-code flow) for
/// access + refresh tokens at AAD's <c>/token</c> endpoint. Stateless POST. Shared by
/// both interactive flows.
/// </summary>
internal sealed class AuthCodeExchanger
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpMessageHandler _handler;
    private readonly TimeSpan _timeout;

    public AuthCodeExchanger() : this(new HttpClientHandler(), null) { }

    internal AuthCodeExchanger(HttpMessageHandler handler, TimeSpan? timeout = null)
    {
        _handler = handler;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Exchanges an auth code for tokens. Returns success+tokens, or failure with the AAD
    /// error code (e.g. <c>invalid_grant</c>, <c>unauthorized_client</c>) so callers can
    /// render specific guidance.
    /// </summary>
    public async Task<TokenExchangeResult> ExchangeCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        string clientId,
        string tenant,
        string authorityHost = AuthorizeRequestBuilder.DefaultAuthorityHost,
        string scopes = AuthorizeRequestBuilder.Scopes,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = scopes,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier,
        };
        return await PostTokenAsync(form, tenant, authorityHost, ct);
    }

    /// <summary>
    /// Exchanges a device code for tokens (used by the device-code flow's polling loop).
    /// <c>authorization_pending</c> and <c>slow_down</c> are returned as failures with the
    /// raw error code so the polling loop can decide whether to keep polling.
    /// </summary>
    public async Task<TokenExchangeResult> ExchangeDeviceCodeAsync(
        string deviceCode,
        string clientId,
        string tenant,
        string authorityHost = AuthorizeRequestBuilder.DefaultAuthorityHost,
        CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
        };
        return await PostTokenAsync(form, tenant, authorityHost, ct);
    }

    private async Task<TokenExchangeResult> PostTokenAsync(
        Dictionary<string, string> form,
        string tenant,
        string authorityHost,
        CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient(_handler, disposeHandler: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var endpoint = string.Create(CultureInfo.InvariantCulture,
                $"https://{authorityHost}/{tenant}/oauth2/v2.0/token");

            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient.PostAsync(endpoint, content, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var parsed = JsonSerializer.Deserialize(body, TwigJsonContext.Default.AuthCodeTokenResponse);
            if (parsed is null)
                return TokenExchangeResult.Fail("invalid_response", "Empty or non-JSON response from token endpoint.");

            if (parsed.AccessToken is { Length: > 0 })
                return TokenExchangeResult.Ok(parsed);

            return TokenExchangeResult.Fail(
                parsed.Error ?? "unknown_error",
                parsed.ErrorDescription ?? "No error description from authorization server.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TokenExchangeResult.Fail("timeout", "Token endpoint did not respond within timeout.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TokenExchangeResult.Fail("network_error", ex.Message);
        }
    }
}

/// <summary>
/// Result of a token endpoint POST. Either the parsed response on success, or an AAD
/// error code + description on failure (caller decides whether to retry, surface to the
/// user, or fall through to a different flow).
/// </summary>
internal sealed record TokenExchangeResult(bool IsSuccess, AuthCodeTokenResponse? Tokens, string? ErrorCode, string? ErrorDescription)
{
    public static TokenExchangeResult Ok(AuthCodeTokenResponse tokens) => new(true, tokens, null, null);
    public static TokenExchangeResult Fail(string code, string description) => new(false, null, code, description);
}

/// <summary>
/// Token endpoint response DTO. Includes <c>id_token</c> (only present when openid scope
/// is requested or for /v2.0 endpoint with profile info) — used to extract identity stamps.
/// </summary>
internal sealed class AuthCodeTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}
