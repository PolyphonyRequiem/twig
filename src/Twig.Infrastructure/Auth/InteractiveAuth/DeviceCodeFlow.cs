using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth.InteractiveAuth;

/// <summary>
/// OAuth 2.0 device authorization grant (RFC 8628) for environments where loopback PKCE
/// is unworkable — headless boxes, SSH sessions, locked-down workstations. The user opens
/// a URL on a separate device, types the displayed code, and consents; meanwhile we poll
/// the token endpoint until success or a terminal error.
///
/// <para>Many enterprise tenants block device code by Conditional Access policy
/// (<c>AADSTS50199</c>, <c>AADSTS165000</c>, <c>AADSTS530032</c>, etc.). When that
/// happens we surface <see cref="InteractiveAuthErrorKind.PolicyBlocked"/> so the
/// command can suggest <c>twig login</c> (PKCE) instead.</para>
/// </summary>
internal sealed class DeviceCodeFlow
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1);

    private readonly AuthCodeExchanger _exchanger;
    private readonly HttpMessageHandler _handler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _requestTimeout;

    public DeviceCodeFlow()
        : this(new AuthCodeExchanger(), new HttpClientHandler(), Task.Delay, null) { }

    internal DeviceCodeFlow(
        AuthCodeExchanger exchanger,
        HttpMessageHandler handler,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? requestTimeout)
    {
        _exchanger = exchanger;
        _handler = handler;
        _delay = delay;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>
    /// Runs the full device-code flow. <paramref name="codeReporter"/> is invoked once with
    /// the user-facing instructions (verification URL + user code) so the caller can render
    /// them via Spectre.Console.
    /// </summary>
    public async Task<InteractiveAuthResult> RunAsync(
        string clientId,
        string tenant,
        Action<DeviceCodeInstructions> codeReporter,
        CancellationToken ct = default)
    {
        var initiate = await InitiateAsync(clientId, tenant, ct);
        if (!initiate.IsSuccess || initiate.Response is null)
        {
            var kind = LooksLikePolicyBlock(initiate.ErrorCode)
                ? InteractiveAuthErrorKind.PolicyBlocked
                : InteractiveAuthErrorKind.AuthorizationServerError;
            return InteractiveAuthResult.Failure(kind,
                $"Device code request failed ({initiate.ErrorCode}): {initiate.ErrorDescription}");
        }

        var dc = initiate.Response;
        if (string.IsNullOrWhiteSpace(dc.DeviceCode) ||
            string.IsNullOrWhiteSpace(dc.UserCode) ||
            string.IsNullOrWhiteSpace(dc.VerificationUri))
        {
            return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.AuthorizationServerError,
                "Device code response missing required fields (device_code/user_code/verification_uri).");
        }

        codeReporter(new DeviceCodeInstructions(
            VerificationUri: dc.VerificationUri,
            UserCode: dc.UserCode,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(dc.ExpiresIn ?? 900),
            Message: dc.Message));

        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, dc.Interval ?? 5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(dc.ExpiresIn ?? 900);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await _delay(pollInterval, ct);

            var exchange = await _exchanger.ExchangeDeviceCodeAsync(dc.DeviceCode, clientId, tenant, ct: ct);

            if (exchange.IsSuccess && exchange.Tokens is not null)
            {
                if (string.IsNullOrWhiteSpace(exchange.Tokens.RefreshToken))
                {
                    return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                        "Device code grant succeeded but no refresh_token was returned.");
                }

                var claims = IdTokenDecoder.Decode(exchange.Tokens.IdToken);
                return InteractiveAuthResult.Success(new TwigRefreshTokenStoreEntry
                {
                    RefreshToken = exchange.Tokens.RefreshToken,
                    ClientId = clientId,
                    TenantId = claims.TenantId ?? tenant,
                    AuthorityHost = AuthorizeRequestBuilder.DefaultAuthorityHost,
                    UserPrincipalName = claims.UserPrincipalName,
                    ObjectId = claims.ObjectId,
                    BootstrappedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    Source = "login-device",
                });
            }

            switch (exchange.ErrorCode)
            {
                case "authorization_pending":
                    // User hasn't completed the consent yet — keep polling.
                    continue;
                case "slow_down":
                    // Per RFC 8628 §3.5: bump interval by at least 5 seconds and continue.
                    pollInterval += TimeSpan.FromSeconds(5);
                    if (pollInterval < MinPollInterval) pollInterval = MinPollInterval;
                    continue;
                case "expired_token":
                case "code_expired":
                    return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.Timeout,
                        "Device code expired before sign-in completed. Re-run 'twig login --device-code'.");
                case "authorization_declined":
                case "access_denied":
                    return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.AuthorizationServerError,
                        "Sign-in was declined.");
                default:
                    if (LooksLikePolicyBlock(exchange.ErrorCode))
                    {
                        return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.PolicyBlocked,
                            $"Tenant policy blocked the device code grant ({exchange.ErrorCode}): {exchange.ErrorDescription}");
                    }
                    return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.TokenExchangeFailed,
                        $"Device code token poll failed ({exchange.ErrorCode}): {exchange.ErrorDescription}");
            }
        }

        return InteractiveAuthResult.Failure(InteractiveAuthErrorKind.Timeout,
            "Device code expired before sign-in completed. Re-run 'twig login --device-code'.");
    }

    private async Task<DeviceCodeInitResult> InitiateAsync(string clientId, string tenant, CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient(_handler, disposeHandler: false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_requestTimeout);

            var endpoint = string.Create(CultureInfo.InvariantCulture,
                $"https://{AuthorizeRequestBuilder.DefaultAuthorityHost}/{tenant}/oauth2/v2.0/devicecode");

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = AuthorizeRequestBuilder.Scopes,
            };

            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient.PostAsync(endpoint, content, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            var parsed = JsonSerializer.Deserialize(body, TwigJsonContext.Default.DeviceCodeResponse);
            if (parsed is null)
                return new DeviceCodeInitResult(false, null, "invalid_response", "Empty or non-JSON device code response.");

            if (!string.IsNullOrEmpty(parsed.Error))
                return new DeviceCodeInitResult(false, null, parsed.Error, parsed.ErrorDescription ?? "(no description)");

            if (string.IsNullOrEmpty(parsed.DeviceCode))
                return new DeviceCodeInitResult(false, null, "missing_device_code", "Device code response did not include device_code.");

            return new DeviceCodeInitResult(true, parsed, null, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DeviceCodeInitResult(false, null, "timeout", "Device code endpoint did not respond.");
        }
        catch (Exception ex)
        {
            return new DeviceCodeInitResult(false, null, "network_error", ex.Message);
        }
    }

    private static bool LooksLikePolicyBlock(string? errorCode)
    {
        if (string.IsNullOrEmpty(errorCode)) return false;
        // AAD returns codes like AADSTS530032 / AADSTS50199 / AADSTS165000 when
        // Conditional Access denies the device code grant. We pattern-match the
        // family rather than the specific number so new policy codes are caught.
        return errorCode.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("access_denied", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("AADSTS50", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("AADSTS53", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("AADSTS165", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record DeviceCodeInitResult(bool IsSuccess, DeviceCodeResponse? Response, string? ErrorCode, string? ErrorDescription);
}

/// <summary>User-facing instructions for the device code flow, surfaced via the codeReporter callback.</summary>
internal sealed record DeviceCodeInstructions(
    string VerificationUri,
    string UserCode,
    DateTimeOffset ExpiresAt,
    string? Message);

/// <summary>Device authorization endpoint response (RFC 8628 §3.2).</summary>
internal sealed class DeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_uri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int? Interval { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}
