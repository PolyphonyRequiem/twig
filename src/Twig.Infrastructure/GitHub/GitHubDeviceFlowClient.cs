using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.GitHub;

internal enum GitHubDeviceScope { PublicRepositories, PrivateRepositories }

internal sealed class GitHubDeviceFlowException(string reason)
    : InvalidOperationException($"GitHub device authorization failed: {reason}.");

internal sealed record GitHubDeviceInstructions(Uri VerificationUri, string UserCode);

/// <summary>In-memory protocol result only; not principal attestation, storage or write authority.</summary>
internal sealed class GitHubDeviceGrant(
    string accessToken, string scope, long? expiresIn,
    string? refreshToken = null, long? refreshTokenExpiresIn = null, DateTimeOffset? requestedAt = null)
{
    internal string AccessToken { get; } = accessToken;
    internal string Scope { get; } = scope;
    internal long? ExpiresIn { get; } = expiresIn;
    internal string? RefreshToken { get; } = refreshToken;
    internal long? RefreshTokenExpiresIn { get; } = refreshTokenExpiresIn;
    internal DateTimeOffset? RequestedAt { get; } = requestedAt;
    public override string ToString() => nameof(GitHubDeviceGrant);
}

/// <summary>
/// Explicit github.com device grant. The caller must obtain sign-in consent for the selected
/// scope; this protocol does not establish consent, principal attestation or enrollment.
/// No CLI selection, storage, refresh or browser effects. Response bodies are capped at 16 KiB,
/// requests at 15 seconds, and the complete flow at the server expiry (at most 15 minutes).
/// </summary>
internal sealed class GitHubDeviceFlowClient : IDisposable
{
    internal const string ClientId = "Ov23liZEX3NdjEa13bjo";
    internal const int MaxResponseBytes = 16384;
    private static readonly Uri CodeEndpoint = new("https://github.com/login/device/code");
    private static readonly Uri TokenEndpoint = new("https://github.com/login/oauth/access_token");
    private static readonly Uri VerificationUri = new("https://github.com/login/device");
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(15);
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal GitHubDeviceFlowClient()
        : this(CreateHandler(), TimeProvider.System, Task.Delay) { }

    internal GitHubDeviceFlowClient(HttpMessageHandler handler, TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _clock = clock;
        _delay = delay;
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        UseProxy = false,
    };

    internal async Task<GitHubDeviceGrant> RunAsync(GitHubDeviceScope scope,
        Action<GitHubDeviceInstructions> reportInstructions, CancellationToken ct = default)
    {
        var requestedScope = scope switch
        {
            GitHubDeviceScope.PublicRepositories => "",
            GitHubDeviceScope.PrivateRepositories => "repo",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        return await RunCoreAsync(ClientId, requestedScope, false, reportInstructions, ct).ConfigureAwait(false);
    }

    internal Task<GitHubDeviceGrant> RunAppAsync(string clientId,
        Action<GitHubDeviceInstructions> reportInstructions, CancellationToken ct = default)
    {
        ValidateAppClientId(clientId);
        return RunCoreAsync(clientId, "offline_access", true, reportInstructions, ct);
    }

    internal async Task<GitHubDeviceGrant> RefreshAppAsync(
        string clientId, string refreshToken, CancellationToken ct = default)
    {
        ValidateAppClientId(clientId);
        if (!AppToken(refreshToken, "ghr_"))
            throw new GitHubDeviceFlowException("invalid stored refresh credential; sign in again");
        var requestedAt = _clock.GetUtcNow();
        var response = await SendAsync(TokenEndpoint, new()
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, TwigJsonContext.Default.GitHubDeviceTokenResponse, RequestBudget, ct).ConfigureAwait(false);
        return AppGrant(response, requestedAt);
    }

    internal async Task<GitHubAppActor> ObserveAppActorAsync(string accessToken, CancellationToken ct)
    {
        if (!AppToken(accessToken, "ghu_"))
            throw new GitHubDeviceFlowException("invalid app credential");
        var actor = await SendAsync(new("https://api.github.com/user"), null,
            TwigJsonContext.Default.GitHubAppActor, RequestBudget, ct, accessToken).ConfigureAwait(false);
        if (!Canonical(actor.NodeId) || actor.NodeId!.Length > 256 || actor.NodeId.Any(char.IsWhiteSpace)
            || !Canonical(actor.Login) || actor.Login!.Length > 100 || actor.Login.Any(char.IsWhiteSpace))
            throw new GitHubDeviceFlowException("authenticated actor observation is incomplete");
        return actor;
    }

    internal static void ValidateAppClientId(string clientId)
    {
        if (!clientId.StartsWith("Iv", StringComparison.Ordinal) || clientId.Length is < 10 or > 100
            || clientId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new GitHubDeviceFlowException("configure the registered GitHub App client ID before sign-in");
    }

    private async Task<GitHubDeviceGrant> RunCoreAsync(string clientId, string requestedScope,
        bool app, Action<GitHubDeviceInstructions> reportInstructions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reportInstructions);
        ct.ThrowIfCancellationRequested();
        // Include the initial request in the monotonic total, never restart expiry per poll.
        var started = _clock.GetTimestamp();
        var total = TimeSpan.FromMinutes(15);
        var code = await SendAsync(CodeEndpoint, new()
        {
            ["client_id"] = clientId,
            ["scope"] = requestedScope,
        }, TwigJsonContext.Default.GitHubDeviceAuthorizationResponse, total, ct).ConfigureAwait(false);
        if (code.Error is not null || !Canonical(code.DeviceCode) || code.DeviceCode!.Length > 256
            || code.DeviceCode!.Any(char.IsWhiteSpace)
            || code.UserCode is not { Length: 9 } userCode || userCode[4] != '-'
            || userCode.Where((_, index) => index != 4).Any(c => !char.IsAsciiLetterOrDigit(c))
            || !string.Equals(code.VerificationUri, VerificationUri.AbsoluteUri, StringComparison.Ordinal)
            || code.ExpiresIn is not (>= 1 and <= 900) || code.Interval is not (>= 1 and <= 900))
            throw new GitHubDeviceFlowException("invalid device response");
        total = TimeSpan.FromSeconds(code.ExpiresIn.Value);
        EnsureRemaining(started, total);
        reportInstructions(new(VerificationUri, userCode));
        var interval = TimeSpan.FromSeconds(code.Interval.Value);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = EnsureRemaining(started, total);
            if (interval >= remaining)
                throw new GitHubDeviceFlowException("device authorization expired");
            await _delay(interval, ct).ConfigureAwait(false);
            remaining = EnsureRemaining(started, total);
            var requestedAt = _clock.GetUtcNow();
            var token = await SendAsync(TokenEndpoint, new()
            {
                ["client_id"] = clientId,
                ["device_code"] = code.DeviceCode!,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }, TwigJsonContext.Default.GitHubDeviceTokenResponse, remaining, ct).ConfigureAwait(false);
            EnsureRemaining(started, total);
            if (token.Error is not null)
            {
                if (token.AccessToken is not null || token.RefreshToken is not null)
                    throw new GitHubDeviceFlowException("ambiguous token response");
                switch (token.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        if (token.Interval is { } serverInterval)
                        {
                            if (serverInterval is < 1 or > 900)
                                throw new GitHubDeviceFlowException("invalid polling interval");
                            interval = TimeSpan.FromSeconds(Math.Max(interval.TotalSeconds, serverInterval));
                        }
                        continue;
                    case "expired_token":
                    case "token_expired":
                        throw new GitHubDeviceFlowException("device authorization expired");
                    case "access_denied":
                        throw new GitHubDeviceFlowException("sign-in declined");
                    default:
                        throw new GitHubDeviceFlowException("authorization server refused the request");
                }
            }
            if (app)
                return AppGrant(token, requestedAt);
            if (!Canonical(token.AccessToken) || token.AccessToken!.Length > 4096
                || token.AccessToken!.Any(char.IsWhiteSpace)
                || !string.Equals(token.TokenType, "bearer", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(token.Scope, requestedScope, StringComparison.Ordinal)
                || token.ExpiresIn is <= 0)
                throw new GitHubDeviceFlowException("invalid token response");
            return new(token.AccessToken!, requestedScope, token.ExpiresIn);
        }
    }

    private static GitHubDeviceGrant AppGrant(GitHubDeviceTokenResponse token, DateTimeOffset requestedAt)
    {
        if (token.Error is not null || !AppToken(token.AccessToken, "ghu_")
            || !AppToken(token.RefreshToken, "ghr_")
            || token.ExpiresIn is not (>= 1 and <= 28800)
            || token.RefreshTokenExpiresIn is not (>= 1 and <= 15897600)
            || !string.Equals(token.TokenType, "bearer", StringComparison.OrdinalIgnoreCase)
            || token.Scope != "")
            throw new GitHubDeviceFlowException("invalid or refused expiring app grant; sign in again");
        return new(token.AccessToken!, "", token.ExpiresIn, token.RefreshToken, token.RefreshTokenExpiresIn, requestedAt);
    }

    internal static bool AppToken(string? value, string prefix) =>
        value is { Length: <= 4096 } && value.Length > prefix.Length
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private TimeSpan EnsureRemaining(long started, TimeSpan total)
    {
        var remaining = total - _clock.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining
            : throw new GitHubDeviceFlowException("device authorization expired");
    }

    private async Task<T> SendAsync<T>(Uri endpoint, Dictionary<string, string>? fields,
        JsonTypeInfo<T> type, TimeSpan remaining, CancellationToken ct, string? bearer = null)
    {
        using var budget = new CancellationTokenSource(remaining < RequestBudget ? remaining : RequestBudget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        using var request = new HttpRequestMessage(fields is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("twig-cli");
        if (fields is not null)
            request.Content = new FormUrlEncodedContent(fields);
        if (bearer is not null)
            request.Headers.Authorization = new("Bearer", bearer);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new GitHubDeviceFlowException("unexpected HTTP status");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new GitHubDeviceFlowException("unexpected response content type");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new GitHubDeviceFlowException("response size exceeded");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBytes + 1];
            try
            {
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), linked.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                }
                if (length > MaxResponseBytes)
                    throw new GitHubDeviceFlowException("response size exceeded");
                linked.Token.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new GitHubDeviceFlowException("invalid JSON response");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name))
                        throw new GitHubDeviceFlowException("ambiguous JSON response");
                return JsonSerializer.Deserialize(buffer.AsSpan(0, length), type)
                    ?? throw new GitHubDeviceFlowException("invalid JSON response");
            }
            finally
            {
                Array.Clear(buffer);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubDeviceFlowException("request deadline exceeded");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException(ct);
        }
        catch (HttpRequestException)
        {
            throw new GitHubDeviceFlowException("HTTP transport unavailable");
        }
        catch (IOException)
        {
            throw new GitHubDeviceFlowException("response transport unavailable");
        }
        catch (JsonException)
        {
            throw new GitHubDeviceFlowException("invalid JSON response");
        }
    }

    private static bool Canonical(string? value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && !value.Any(char.IsControl);

    public void Dispose() => _http.Dispose();
}

internal sealed class GitHubAppActor
{
    [JsonPropertyName("node_id")] public string? NodeId { get; set; }
    [JsonPropertyName("login")] public string? Login { get; set; }
}

internal sealed class GitHubDeviceAuthorizationResponse
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    public override string ToString() => nameof(GitHubDeviceAuthorizationResponse);
}

internal sealed class GitHubDeviceTokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("expires_in")] public long? ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("refresh_token_expires_in")] public long? RefreshTokenExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    public override string ToString() => nameof(GitHubDeviceTokenResponse);
}
