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
/// Root DTO for the MSAL token cache JSON file written by Azure CLI.
/// </summary>
internal sealed class MsalTokenCache
{
    [JsonPropertyName("AccessToken")]
    public Dictionary<string, MsalAccessTokenEntry>? AccessToken { get; set; }
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

    private readonly IAuthenticationProvider _inner;
    private readonly string _cacheFilePath;
    private readonly Func<string, CancellationToken, Task<string?>> _fileReader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cacheExpiry;

    public MsalCacheTokenProvider(
        IAuthenticationProvider inner,
        string? cacheFilePath = null,
        Func<string, CancellationToken, Task<string?>>? fileReader = null,
        Func<DateTimeOffset>? clock = null)
    {
        _inner = inner;
        _cacheFilePath = cacheFilePath ?? DefaultCachePath;
        _fileReader = fileReader ?? DefaultFileReaderAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
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
            try
            {
                var json = await _fileReader(_cacheFilePath, ct);
                if (json is not null)
                {
                    var cache = JsonSerializer.Deserialize(json, TwigJsonContext.Default.MsalTokenCache);
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
                // Any failure reading/parsing cache — fall through to inner provider
            }

            // 3. Fallback to inner provider (az CLI)
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
}
