using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Acquires Azure DevOps access tokens via the AAD MSAL cache (written by Azure CLI's
/// <c>az login</c>) plus a direct HTTP refresh exchange — no shell-out to <c>az</c>.
///
/// Layered cache strategy:
///   1. In-memory (50-min TTL, pre-validated)
///   2. <c>~/.twig/.token-cache</c> cross-process file cache (audience-validated on read)
///   3. <c>~/.azure/msal_token_cache.json</c> access tokens (audience-validated on read)
///   4. AAD <c>/oauth2/v2.0/token</c> refresh exchange via <see cref="MsalTokenRefresher"/>
///
/// Every cached token is decoded as a JWT and rejected unless its <c>aud</c> claim
/// matches the ADO API resource ID. This defends against stale/wrong-audience tokens
/// being silently reused — the bug class behind issue #164.
/// </summary>
internal sealed class AdoAccessTokenProvider : IAuthenticationProvider
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private static readonly string DefaultMsalCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".azure", "msal_token_cache.json");

    private readonly TwigTokenFileCache _fileCache;
    private readonly MsalTokenRefresher _refresher;
    private readonly string _msalCachePath;
    private readonly Func<string, CancellationToken, Task<string?>> _msalCacheReader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cacheExpiry;

    public AdoAccessTokenProvider()
        : this(null, null, null, null, null)
    {
    }

    internal AdoAccessTokenProvider(
        string? msalCachePath = null,
        Func<string, CancellationToken, Task<string?>>? msalCacheReader = null,
        Func<DateTimeOffset>? clock = null,
        MsalTokenRefresher? refresher = null,
        TwigTokenFileCache? fileCache = null)
    {
        _msalCachePath = msalCachePath ?? DefaultMsalCachePath;
        _msalCacheReader = msalCacheReader ?? DefaultMsalCacheReaderAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _refresher = refresher ?? new MsalTokenRefresher();
        _fileCache = fileCache ?? new TwigTokenFileCache();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var now = _clock();

            // 1. In-memory cache (already audience-validated when stored)
            if (_cachedToken is not null && now < _cacheExpiry)
                return _cachedToken;

            // 2. Cross-process file cache — validate audience before trusting
            var (fileToken, fileExpiry) = _fileCache.TryRead();
            if (fileToken is not null && now + ExpiryBuffer < fileExpiry
                && JwtAccessTokenInspector.HasValidAdoAudience(fileToken))
            {
                _cachedToken = fileToken;
                _cacheExpiry = fileExpiry;
                return fileToken;
            }

            // File-cache hit but wrong audience or expired? Wipe it so we don't
            // poison subsequent reads in this process or others.
            if (fileToken is not null)
                _fileCache.TryDelete();

            // 3. MSAL cache file (written by az CLI) — validate audience per entry
            var msalCache = await TryReadMsalCacheAsync(ct);
            if (msalCache?.AccessToken is { } accessTokens)
            {
                var (token, tokenExpiry) = FindBestValidatedToken(accessTokens, now);
                if (token is not null)
                {
                    StoreAndPersist(token, tokenExpiry, now);
                    return token;
                }
            }

            // 4. HTTP refresh using refresh token from MSAL cache — request ADO scope explicitly
            if (msalCache is not null)
            {
                var refreshContext = MsalTokenRefresher.FindRefreshContext(msalCache);
                if (refreshContext is var (rt, clientId, tenantId, authorityHost))
                {
                    var (refreshedToken, isInvalidGrant) = await _refresher.TryRefreshAsync(
                        rt, clientId, tenantId, authorityHost, ct);

                    if (refreshedToken is not null
                        && JwtAccessTokenInspector.HasValidAdoAudience(refreshedToken))
                    {
                        var refreshedExpiry = ResolveExpiryFromJwt(refreshedToken, now);
                        StoreAndPersist(refreshedToken, refreshedExpiry, now);
                        return refreshedToken;
                    }

                    if (isInvalidGrant)
                    {
                        _cachedToken = null;
                        _cacheExpiry = default;
                    }
                }
            }

            throw new AdoAuthenticationException(
                $"Could not acquire an Azure DevOps access token from the MSAL cache at {_msalCachePath}. " +
                "Run 'az login --scope 499b84ac-1321-427f-aa17-267ca6975798/.default' to refresh credentials, " +
                "then retry. If you keep seeing this, run 'twig auth status' to inspect the cached token.");
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
        _fileCache.TryDelete();
    }

    /// <summary>
    /// Returns the longest-lived MSAL access token whose JWT audience matches the ADO API
    /// resource. Entries that look right by Target string but decode to a different
    /// audience are rejected — that's the bug class behind issue #164.
    /// </summary>
    private static (string? Token, DateTimeOffset Expiry) FindBestValidatedToken(
        Dictionary<string, MsalAccessTokenEntry> accessTokens,
        DateTimeOffset now)
    {
        string? bestToken = null;
        var bestExpiry = DateTimeOffset.MinValue;

        foreach (var entry in accessTokens.Values)
        {
            if (entry.Secret is null) continue;

            // Coarse Target prefilter — cheap; final audience check happens on the JWT.
            if (entry.Target is null
                || !entry.Target.Contains(JwtAccessTokenInspector.AdoResourceId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.ExpiresOn is null
                || !long.TryParse(entry.ExpiresOn, out var epoch))
                continue;

            var expiry = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (expiry <= now + ExpiryBuffer) continue;

            if (!JwtAccessTokenInspector.HasValidAdoAudience(entry.Secret))
                continue;

            if (expiry > bestExpiry)
            {
                bestExpiry = expiry;
                bestToken = entry.Secret;
            }
        }

        return (bestToken, bestExpiry);
    }

    private void StoreAndPersist(string token, DateTimeOffset tokenExpiry, DateTimeOffset now)
    {
        _cachedToken = token;
        // Use the earlier of our standard TTL or the actual token expiry minus buffer.
        _cacheExpiry = tokenExpiry < now + TokenTtl
            ? tokenExpiry - ExpiryBuffer
            : now + TokenTtl;
        _fileCache.TryWrite(token, _cacheExpiry);
    }

    private static DateTimeOffset ResolveExpiryFromJwt(string token, DateTimeOffset now)
    {
        // Prefer the JWT's actual exp claim; fall back to standard TTL if absent.
        var info = JwtAccessTokenInspector.TryDecode(token);
        return info?.ExpiresAt ?? now + TokenTtl;
    }

    private async Task<MsalTokenCache?> TryReadMsalCacheAsync(CancellationToken ct)
    {
        try
        {
            var json = await _msalCacheReader(_msalCachePath, ct);
            if (json is null) return null;

            return System.Text.Json.JsonSerializer.Deserialize(
                json, Serialization.TwigJsonContext.Default.MsalTokenCache);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> DefaultMsalCacheReaderAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        // FileShare.ReadWrite avoids contention with concurrent Azure CLI writes.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
