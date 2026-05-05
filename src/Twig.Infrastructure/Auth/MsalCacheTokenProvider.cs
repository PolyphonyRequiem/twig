using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// MSAL token cache entry for a single access token.
/// </summary>
internal sealed class MsalAccessTokenEntry
{
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("expires_on")]
    public string? ExpiresOn { get; set; }
}

/// <summary>
/// MSAL token cache entry for a refresh token.
/// </summary>
internal sealed class MsalRefreshTokenEntry
{
    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("home_account_id")]
    public string? HomeAccountId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

/// <summary>
/// MSAL token cache entry for an account.
/// </summary>
internal sealed class MsalAccountEntry
{
    [JsonPropertyName("home_account_id")]
    public string? HomeAccountId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("realm")]
    public string? Realm { get; set; }
}

/// <summary>
/// OAuth2 token endpoint response (subset of fields we need).
/// </summary>
internal sealed class TokenRefreshResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Root DTO for the MSAL token cache JSON file written by Azure CLI.
/// </summary>
internal sealed class MsalTokenCache
{
    [JsonPropertyName("AccessToken")]
    public Dictionary<string, MsalAccessTokenEntry>? AccessToken { get; set; }

    [JsonPropertyName("RefreshToken")]
    public Dictionary<string, MsalRefreshTokenEntry>? RefreshToken { get; set; }

    [JsonPropertyName("Account")]
    public Dictionary<string, MsalAccountEntry>? Account { get; set; }
}

/// <summary>
/// Decorator over <see cref="IAuthenticationProvider"/> that reads the MSAL token cache
/// file written by Azure CLI before falling back to the inner provider.
/// This avoids shelling out to <c>az account get-access-token</c> when a valid cached
/// token already exists, saving ~100–300ms of process-creation overhead (DD-21, DD-22, NFR-5).
/// </summary>
internal sealed class MsalCacheTokenProvider : IAuthenticationProvider
{
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".azure", "msal_token_cache.json");

    private static readonly string DefaultTokenCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".twig", ".token-cache");

    private readonly IAuthenticationProvider _inner;
    private readonly MsalTokenRefresher _refresher;
    private readonly string _cacheFilePath;
    private readonly Func<string, CancellationToken, Task<string?>> _fileReader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, DateTimeOffset>? _tokenCacheWriter;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cacheExpiry;

    public MsalCacheTokenProvider(
        IAuthenticationProvider inner,
        string? cacheFilePath = null,
        Func<string, CancellationToken, Task<string?>>? fileReader = null,
        Func<DateTimeOffset>? clock = null,
        MsalTokenRefresher? refresher = null,
        Action<string, DateTimeOffset>? tokenCacheWriter = null)
    {
        _inner = inner;
        _cacheFilePath = cacheFilePath ?? DefaultCachePath;
        _fileReader = fileReader ?? DefaultFileReaderAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _refresher = refresher ?? new MsalTokenRefresher();
        _tokenCacheWriter = tokenCacheWriter ?? DefaultTokenCacheWriter;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var now = _clock();

            // 1. In-memory cache (50-min TTL)
            if (_cachedToken is not null && now < _cacheExpiry)
                return _cachedToken;

            // 2. Try reading MSAL cache file
            MsalTokenCache? cache = null;
            try
            {
                var json = await _fileReader(_cacheFilePath, ct);
                if (json is not null)
                {
                    cache = JsonSerializer.Deserialize(json, TwigJsonContext.Default.MsalTokenCache);
                    if (cache?.AccessToken is { } accessTokens)
                    {
                        var (token, tokenExpiry) = FindBestToken(accessTokens, now);
                        if (token is not null)
                        {
                            _cachedToken = token;
                            // Use the earlier of our standard TTL or the actual token expiry (minus buffer)
                            _cacheExpiry = tokenExpiry < now + TokenTtl
                                ? tokenExpiry - ExpiryBuffer
                                : now + TokenTtl;
                            return token;
                        }
                    }
                }
            }
            catch
            {
                // Any failure reading/parsing cache — fall through to refresh/inner
            }

            // 3. Try direct HTTP refresh using refresh token from MSAL cache
            if (cache is not null)
            {
                var refreshContext = MsalTokenRefresher.FindRefreshContext(cache);
                if (refreshContext is var (rt, clientId, tenantId, authorityHost))
                {
                    var (refreshedToken, isInvalidGrant) = await _refresher.TryRefreshAsync(
                        rt, clientId, tenantId, authorityHost, ct);

                    if (refreshedToken is not null)
                    {
                        _cachedToken = refreshedToken;
                        _cacheExpiry = now + TokenTtl;
                        TryWriteTokenCache(refreshedToken, _cacheExpiry);
                        return refreshedToken;
                    }

                    if (isInvalidGrant)
                    {
                        // Refresh token is revoked — clear stale state before falling through
                        _cachedToken = null;
                        _cacheExpiry = default;
                    }
                }
            }

            // 4. Fallback to inner provider (az CLI)
            var innerToken = await _inner.GetAccessTokenAsync(ct);
            _cachedToken = innerToken;
            _cacheExpiry = now + TokenTtl;
            return innerToken;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateToken()
    {
        _cachedToken = null;
        _cacheExpiry = default;
        _inner.InvalidateToken();
    }

    /// <summary>
    /// Finds the best (longest-lived) ADO token from the MSAL cache that is still valid
    /// with at least 5 minutes of remaining lifetime.
    /// Returns the raw secret string (NOT Bearer-prefixed — DD-21) and its expiry.
    /// </summary>
    private (string? token, DateTimeOffset expiry) FindBestToken(Dictionary<string, MsalAccessTokenEntry> accessTokens, DateTimeOffset now)
    {
        string? bestToken = null;
        DateTimeOffset bestExpiry = DateTimeOffset.MinValue;

        foreach (var entry in accessTokens.Values)
        {
            if (entry.Target is null || !entry.Target.Contains(AdoResourceId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.Secret is null)
                continue;

            if (entry.ExpiresOn is null || !long.TryParse(entry.ExpiresOn, out var epoch))
                continue;

            var expiry = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (expiry <= now + ExpiryBuffer)
                continue;

            if (expiry > bestExpiry)
            {
                bestExpiry = expiry;
                bestToken = entry.Secret;
            }
        }

        return (bestToken, bestExpiry);
    }

    /// <summary>
    /// Default file reader that uses <see cref="FileStream"/> with <see cref="FileShare.ReadWrite"/>
    /// to avoid locking conflicts with concurrent Azure CLI writes.
    /// </summary>
    private static async Task<string?> DefaultFileReaderAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Writes the refreshed token to the cross-process file cache for sharing with other twig processes.
    /// Best-effort — failures are silently ignored.
    /// </summary>
    private void TryWriteTokenCache(string token, DateTimeOffset expiry)
    {
        try
        {
            _tokenCacheWriter?.Invoke(token, expiry);
        }
        catch
        {
            // Best effort — if we can't write the cache, the in-memory cache still works
        }
    }

    /// <summary>
    /// Default token cache writer that persists to <c>~/.twig/.token-cache</c>.
    /// Uses atomic write (tmp + rename) with restricted file permissions.
    /// </summary>
    private static void DefaultTokenCacheWriter(string token, DateTimeOffset expiry)
    {
        var dir = Path.GetDirectoryName(DefaultTokenCachePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var tmpPath = DefaultTokenCachePath + ".tmp";
        File.WriteAllText(tmpPath,
            $"{expiry.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n{token}\n");
        File.Move(tmpPath, DefaultTokenCachePath, overwrite: true);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DefaultTokenCachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
