using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Acquires Azure DevOps access tokens via twig's own refresh-token store, bootstrapped
/// once from the Azure CLI MSAL cache.
///
/// <para>
/// Layered cache strategy:
/// <list type="number">
/// <item>In-memory cache (50-min TTL, pre-validated).</item>
/// <item><c>~/.twig/.token-cache</c> cross-process file cache (audience-validated on read).</item>
/// <item><c>~/.twig/.refresh-token</c> twig-owned refresh token, exchanged via direct AAD HTTP.
///       Bootstrapped once from <c>~/.azure/msal_token_cache.json</c> if absent — never read again.</item>
/// </list>
/// </para>
///
/// Every cached token is decoded as a JWT and rejected unless its <c>aud</c> claim matches
/// the ADO API resource ID. The MSAL cache is consulted only for the one-time refresh-token
/// bootstrap; we never trust az's access tokens. This is what made #164's bug class
/// structurally impossible.
/// </summary>
internal sealed class AdoAccessTokenProvider : IAuthenticationProvider
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private static readonly string DefaultMsalCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".azure", "msal_token_cache.json");

    private readonly TwigTokenFileCache _fileCache;
    private readonly TwigRefreshTokenStore _refreshStore;
    private readonly ITokenRefresher _refresher;
    private readonly string _msalCachePath;
    private readonly Func<string, CancellationToken, Task<string?>> _msalCacheReader;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cacheExpiry;

    public AdoAccessTokenProvider()
        : this(null, null, null, null, null, null)
    {
    }

    internal AdoAccessTokenProvider(
        string? msalCachePath = null,
        Func<string, CancellationToken, Task<string?>>? msalCacheReader = null,
        Func<DateTimeOffset>? clock = null,
        ITokenRefresher? refresher = null,
        TwigTokenFileCache? fileCache = null,
        TwigRefreshTokenStore? refreshStore = null)
    {
        _msalCachePath = msalCachePath ?? DefaultMsalCachePath;
        _msalCacheReader = msalCacheReader ?? DefaultMsalCacheReaderAsync;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _refresher = refresher ?? new MsalTokenRefresher();
        _fileCache = fileCache ?? new TwigTokenFileCache();
        _refreshStore = refreshStore ?? new TwigRefreshTokenStore();
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

            // 3. Refresh via twig's own refresh-token store. If the store doesn't exist,
            // bootstrap it once from the MSAL cache. After this call we never touch
            // ~/.azure/ again unless the user explicitly re-bootstraps via 'twig auth clear'.
            var minted = await TryMintFromRefreshStoreAsync(now, ct);
            if (minted is not null)
                return minted;

            // Bootstrap may not have been possible (no MSAL cache, no refresh token there,
            // or refresh failed). Throw with actionable guidance.
            throw new AdoAuthenticationException(
                $"Could not acquire an Azure DevOps access token. " +
                $"Run 'az login --scope 499b84ac-1321-427f-aa17-267ca6975798/.default' " +
                $"then 'twig auth clear' to re-bootstrap. " +
                $"For details run 'twig auth status'.");
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
    /// Mint an access token using the refresh-token store. If the store is empty, attempts
    /// a one-time bootstrap from the MSAL cache. On <c>invalid_grant</c> against a previously
    /// stored entry, attempts one re-bootstrap from MSAL — the user may have re-run
    /// <c>az login</c> after their refresh token was revoked.
    /// </summary>
    private async Task<string?> TryMintFromRefreshStoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        var entry = _refreshStore.TryRead();
        var hadStoredEntry = entry is not null;
        entry ??= await BootstrapFromMsalAsync(ct);
        if (entry is null) return null;

        var (minted, isInvalidGrant) = await TryRefreshAndStoreAsync(entry, now, ct);
        if (minted is not null) return minted;

        // Only re-bootstrap when a pre-existing stored entry was rejected by AAD.
        // Plain failures (network, transient) must not silently re-bootstrap.
        // A failure on a freshly bootstrapped entry must not loop either.
        if (hadStoredEntry && isInvalidGrant)
        {
            var rebooted = await BootstrapFromMsalAsync(ct);
            if (rebooted is null) return null;
            var (retryMinted, _) = await TryRefreshAndStoreAsync(rebooted, now, ct);
            return retryMinted;
        }

        return null;
    }

    private async Task<(string? Token, bool IsInvalidGrant)> TryRefreshAndStoreAsync(
        TwigRefreshTokenStoreEntry entry, DateTimeOffset now, CancellationToken ct)
    {
        if (entry.RefreshToken is not { Length: > 0 } rt
            || entry.ClientId is not { Length: > 0 } clientId
            || entry.TenantId is not { Length: > 0 } tenantId
            || entry.AuthorityHost is not { Length: > 0 } authorityHost)
            return (null, false);

        var (refreshedToken, isInvalidGrant) = await _refresher.TryRefreshAsync(
            rt, clientId, tenantId, authorityHost, ct);

        if (refreshedToken is null)
        {
            if (isInvalidGrant)
            {
                _cachedToken = null;
                _cacheExpiry = default;
                _refreshStore.TryDelete();
            }
            return (null, isInvalidGrant);
        }

        if (!JwtAccessTokenInspector.HasValidAdoAudience(refreshedToken))
            return (null, false);

        var refreshedExpiry = ResolveExpiryFromJwt(refreshedToken, now);
        StoreAndPersist(refreshedToken, refreshedExpiry, now);
        return (refreshedToken, false);
    }

    /// <summary>
    /// One-time bootstrap: read the MSAL cache, extract the refresh-token context, persist
    /// it to twig's own store. Returns the new entry on success, null on any failure.
    /// </summary>
    private async Task<TwigRefreshTokenStoreEntry?> BootstrapFromMsalAsync(CancellationToken ct)
    {
        var cache = await TryReadMsalCacheAsync(ct);
        if (cache is null) return null;

        var ctx = MsalTokenRefresher.FindRefreshContext(cache);
        if (ctx is not var (rt, clientId, tenantId, authorityHost)) return null;

        // Identity stamp: pick the matching account's home_account_id (oid.tenant) if present.
        // Best-effort — diagnostics-only, not a security boundary.
        var (upn, oid) = ResolveIdentityFromMsalCache(cache, tenantId);

        var entry = new TwigRefreshTokenStoreEntry
        {
            RefreshToken = rt,
            ClientId = clientId,
            TenantId = tenantId,
            AuthorityHost = authorityHost,
            UserPrincipalName = upn,
            ObjectId = oid,
            BootstrappedAt = DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture),
            Source = "azcli",
        };
        _refreshStore.TryWrite(entry);
        return entry;
    }

    /// <summary>
    /// Walks the Account entries to extract a UPN/OID for the bootstrapped tenant.
    /// MSAL doesn't expose UPN consistently across versions; OID falls out of home_account_id.
    /// </summary>
    private static (string? Upn, string? Oid) ResolveIdentityFromMsalCache(MsalTokenCache cache, string tenantId)
    {
        if (cache.Account is not { Count: > 0 } accounts)
            return (null, null);

        foreach (var account in accounts.Values)
        {
            if (!string.Equals(account.Realm, tenantId, StringComparison.OrdinalIgnoreCase))
                continue;

            string? oid = null;
            if (account.HomeAccountId is { } hai)
            {
                var dot = hai.IndexOf('.', StringComparison.Ordinal);
                if (dot > 0) oid = hai[..dot];
            }
            return (Upn: null, Oid: oid);
        }

        return (null, null);
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
