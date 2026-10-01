using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// <see cref="IAuthenticationProvider"/> implementation produced by
/// <see cref="ConnectionBindingService"/>. Bound to one registered
/// <see cref="AuthenticationIdentity"/> and the credential blob stored under
/// its opaque <c>credential_ref</c> — never bootstraps from the MSAL cache,
/// never falls back to a sibling identity, and refuses any minted or cached
/// token whose principal (audience / tenant / oid) does not match the
/// stamped <see cref="AuthenticationIdentity"/> it was built against.
/// <para>
/// The actionable error on wrong-account mismatch names the configured
/// identity and the observed principal so the caller can fix the binding
/// without reading the token itself.
/// </para>
/// </summary>
internal sealed class BoundConnectionAuthenticationProvider : IAuthenticationProvider
{
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly AuthenticationIdentity _identity;
    private readonly TwigRefreshTokenStore _refreshStore;
    private readonly TwigTokenFileCache _tokenCache;
    private readonly ITokenRefresher _refresher;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cacheExpiry;

    public BoundConnectionAuthenticationProvider(
        AuthenticationIdentity identity,
        TwigRefreshTokenStore refreshStore,
        TwigTokenFileCache tokenCache,
        ITokenRefresher refresher,
        TimeProvider? clock = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _refreshStore = refreshStore ?? throw new ArgumentNullException(nameof(refreshStore));
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();

            // 1. In-memory cache — audience/principal already validated when stored.
            if (_cachedToken is not null && now < _cacheExpiry)
                return _cachedToken;

            // 2. Per-credential file cache — audience and principal validated
            //    before trust so a stale/wrong-account carry-over from a
            //    previous process cannot leak through.
            var (fileToken, fileExpiry) = _tokenCache.TryRead();
            var fileInfo = JwtAccessTokenInspector.TryDecode(fileToken);
            if (fileToken is not null && !HasExpectedPrincipal(fileInfo))
                throw PrincipalMismatchException(fileToken);
            if (fileToken is not null && fileInfo is { IsValidAdoAudience: true }
                && fileInfo.IsNotExpired(now, ExpiryBuffer) && now + ExpiryBuffer < fileExpiry)
            {
                _cachedToken = fileToken;
                _cacheExpiry = fileInfo.ExpiresAt!.Value < fileExpiry ? fileInfo.ExpiresAt.Value : fileExpiry;
                return fileToken;
            }

            // Expired or wrong-resource tokens can be renewed only through
            // this identity's verified refresh credential.
            if (fileToken is not null)
                _tokenCache.TryDelete();

            // 3. Refresh via the per-credential refresh-token store. No MSAL
            //    bootstrap and no sibling-store fallback — a bound provider
            //    only speaks for the identity it was built against.
            var entry = _refreshStore.TryRead()
                ?? throw NoRefreshEntryException();

            EnsureEntryMatchesIdentity(entry);

            if (entry.RefreshToken is not { Length: > 0 } rt
                || entry.ClientId is not { Length: > 0 } clientId
                || entry.TenantId is not { Length: > 0 } tenantId
                || entry.AuthorityHost is not { Length: > 0 } authorityHost)
                throw IncompleteEntryException();

            var (minted, rotatedRt, invalidGrant) = await _refresher.TryRefreshAsync(
                rt, clientId, tenantId, authorityHost, ct).ConfigureAwait(false);

            if (minted is null)
            {
                _cachedToken = null;
                _cacheExpiry = default;
                _tokenCache.TryDelete();
                throw invalidGrant
                    ? new AdoAuthenticationException(
                        $"AAD rejected the refresh token for identity '{_identity.Name}' (invalid_grant). " +
                        $"Run 'twig auth login --identity {_identity.Name}' to collect a fresh refresh token for this bound identity.")
                    : new AdoAuthenticationException(
                        $"Could not exchange the refresh token for identity '{_identity.Name}' into an ADO access token. " +
                        $"Check network connectivity and the configured authority host ({_identity.AuthorityHost}).");
            }

            if (!TryAcceptMintedToken(minted))
            {
                _tokenCache.TryDelete();
                throw PrincipalMismatchException(minted);
            }

            if (rotatedRt is not null && !string.Equals(rotatedRt, entry.RefreshToken, StringComparison.Ordinal))
            {
                entry.RefreshToken = rotatedRt;
                _refreshStore.TryWrite(entry);
            }

            var expiry = ResolveExpiry(minted, now);
            _cachedToken = minted;
            _cacheExpiry = expiry < now + TokenTtl ? expiry - ExpiryBuffer : now + TokenTtl;
            _tokenCache.TryWrite(minted, _cacheExpiry);
            return minted;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void InvalidateToken()
    {
        _cachedToken = null;
        _cacheExpiry = default;
        _tokenCache.TryDelete();
    }

    /// <summary>Validate a minted or cached token against the bound
    /// identity. Audience + tenant + oid must all line up before the token
    /// is trusted; a wrong-account-but-valid-audience token is rejected
    /// here with no network side effect.</summary>
    private bool TryAcceptMintedToken(string token)
    {
        var info = JwtAccessTokenInspector.TryDecode(token);
        return info is { IsValidAdoAudience: true }
            && info.IsNotExpired(_clock.GetUtcNow(), ExpiryBuffer)
            && HasExpectedPrincipal(info);
    }

    private bool HasExpectedPrincipal(JwtTokenInfo? info)
    {
        if (info is null) return false;
        if (string.IsNullOrEmpty(info.TenantId)
            || !string.Equals(info.TenantId, _identity.TenantId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(info.ObjectId)
            || !string.Equals(info.ObjectId, _identity.ObjectId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(info.Issuer)
            || !string.Equals(info.Issuer, _identity.Issuer, StringComparison.Ordinal))
            return false;
        return true;
    }

    private void EnsureEntryMatchesIdentity(TwigRefreshTokenStoreEntry entry)
    {
        // The refresh entry must stamp the same principal as the identity row
        // this provider is bound to. Divergence means the credentials
        // directory was tampered with (or a sibling process rewrote the file
        // under this credential_ref). Refuse before spending a network call.
        if (!string.Equals(entry.TenantId, _identity.TenantId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(entry.ObjectId)
            || !string.Equals(entry.ObjectId, _identity.ObjectId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.AuthorityHost, _identity.AuthorityHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectionIdentityMismatchException(
                $"Stored refresh entry for identity '{_identity.Name}' no longer matches the registered principal " +
                $"(expected tenant={_identity.TenantId}, oid={_identity.ObjectId}, authority={_identity.AuthorityHost}; " +
                $"observed tenant={entry.TenantId ?? "(missing)"}, oid={entry.ObjectId ?? "(missing)"}, authority={entry.AuthorityHost ?? "(missing)"}). " +
                $"Re-run 'twig auth login --identity {_identity.Name}' to restore the credential or bind a different identity.");
        }
    }

    private AdoAuthenticationException NoRefreshEntryException() =>
        new($"Identity '{_identity.Name}' has no credential blob under credential_ref '{_identity.CredentialRef}'. " +
            $"Run 'twig auth login --identity {_identity.Name}' to re-register this identity's credential.");

    private AdoAuthenticationException IncompleteEntryException() =>
        new($"Credential blob for identity '{_identity.Name}' is missing required fields (refresh_token/client_id/tenant_id/authority_host). " +
            $"Re-run 'twig auth login --identity {_identity.Name}' to re-register the credential.");

    private ConnectionIdentityMismatchException PrincipalMismatchException(string minted)
    {
        var info = JwtAccessTokenInspector.TryDecode(minted);
        var observedTid = info?.TenantId ?? "(missing)";
        var observedOid = info?.ObjectId ?? "(missing)";
        var observedAud = info?.Audience ?? "(missing)";
        return new ConnectionIdentityMismatchException(
            $"Minted token for identity '{_identity.Name}' does not match the bound principal before work HTTP. " +
            $"expected: aud=ADO, tenant={_identity.TenantId}, oid={_identity.ObjectId}; " +
            $"observed: aud={observedAud}, tenant={observedTid}, oid={observedOid}. " +
            $"The credential under '{_identity.CredentialRef}' has been re-issued against a different account. " +
            $"Re-run 'twig auth login --identity {_identity.Name}' or rebind the connection to the correct identity.");
    }

    private static DateTimeOffset ResolveExpiry(string token, DateTimeOffset now)
    {
        var info = JwtAccessTokenInspector.TryDecode(token);
        return info?.ExpiresAt ?? now + TokenTtl;
    }

}
