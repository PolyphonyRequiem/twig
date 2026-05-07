using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="AdoAccessTokenProvider"/>.
/// Covers the bootstrap-once architecture (v0.76+) and the audience-validation guard
/// added in response to issue #164.
/// </summary>
public sealed class AdoAccessTokenProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TwigTokenFileCache _fileCache;
    private readonly TwigRefreshTokenStore _refreshStore;

    public AdoAccessTokenProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fileCache = new TwigTokenFileCache(Path.Combine(_tempDir, ".token-cache"));
        _refreshStore = new TwigRefreshTokenStore(Path.Combine(_tempDir, ".refresh-token"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private AdoAccessTokenProvider CreateProvider(
        string? msalCacheJson = null,
        Func<DateTimeOffset>? clock = null,
        ITokenRefresher? refresher = null,
        Action<int>? msalReadCounter = null)
    {
        var reads = 0;
        return new AdoAccessTokenProvider(
            msalCachePath: Path.Combine(_tempDir, "msal_token_cache.json"),
            msalCacheReader: (_, _) =>
            {
                reads++;
                msalReadCounter?.Invoke(reads);
                return Task.FromResult(msalCacheJson);
            },
            clock: clock,
            refresher: refresher,
            fileCache: _fileCache,
            refreshStore: _refreshStore);
    }

    #region File cache (audience guard for #164)

    [Fact]
    public async Task GetAccessTokenAsync_FileCacheHitWithValidAudience_ReturnsCachedToken()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = JwtTestFactory.Build(audience: JwtTestFactory.AdoResourceId, expiresAt: now.AddMinutes(30));
        _fileCache.TryWrite(jwt, now.AddMinutes(30));

        var provider = CreateProvider(clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe(jwt);
    }

    [Fact]
    public async Task GetAccessTokenAsync_FileCacheHitWithWrongAudience_WipesCacheAndThrows()
    {
        // The exact symptom of issue #164: a wrong-audience token is in the file cache.
        var now = DateTimeOffset.UtcNow;
        var wrongAudienceJwt = JwtTestFactory.BuildWrongAudience(expiresAt: now.AddMinutes(30));
        _fileCache.TryWrite(wrongAudienceJwt, now.AddMinutes(30));

        var provider = CreateProvider(msalCacheJson: null, clock: () => now);

        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());

        // The poisoned file cache must have been wiped so subsequent calls don't re-poison.
        File.Exists(_fileCache.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_FileCacheExpired_FallsThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(1));
        _fileCache.TryWrite(expiredJwt, now.AddMinutes(1)); // Inside the 5-min expiry buffer

        var provider = CreateProvider(msalCacheJson: null, clock: () => now);

        // No MSAL bootstrap available → must throw with guidance.
        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());
    }

    #endregion

    #region Bootstrap-once flow

    [Fact]
    public async Task GetAccessTokenAsync_NoStore_BootstrapsFromMsalAndMintsToken()
    {
        var now = DateTimeOffset.UtcNow;
        var msalJson = JwtTestFactory.BuildMsalCacheJsonWithRefreshToken(
            refreshTokenSecret: "rt-from-msal",
            tenantId: "tenant-A",
            oid: "oid-A");
        var freshJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45), tenantId: "tenant-A");
        var refresher = new FakeTokenRefresher().EnqueueSuccess(freshJwt);

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now, refresher: refresher);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe(freshJwt);
        refresher.Calls.Count.ShouldBe(1);
        refresher.Calls[0].RefreshToken.ShouldBe("rt-from-msal");
        refresher.Calls[0].TenantId.ShouldBe("tenant-A");

        // Bootstrap entry must have been persisted with all fields populated.
        _refreshStore.Exists().ShouldBeTrue();
        var entry = _refreshStore.TryRead().ShouldNotBeNull();
        entry.RefreshToken.ShouldBe("rt-from-msal");
        entry.TenantId.ShouldBe("tenant-A");
        entry.ObjectId.ShouldBe("oid-A");
        entry.Source.ShouldBe("azcli");
        entry.BootstrappedAt.ShouldNotBeNullOrEmpty();
        entry.AuthorityHost.ShouldBe("login.microsoftonline.com");

        // The minted token must have been written to the cross-process file cache.
        File.Exists(_fileCache.Path).ShouldBeTrue();
    }

    [Fact]
    public async Task GetAccessTokenAsync_StoreExists_DoesNotReadMsalCache()
    {
        // Hazard surface guard: once bootstrapped, we must never touch ~/.azure/ again.
        var now = DateTimeOffset.UtcNow;
        _refreshStore.TryWrite(new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "stored-rt",
            ClientId = "client-1",
            TenantId = "tenant-stored",
            AuthorityHost = "login.microsoftonline.com",
            Source = "azcli",
            BootstrappedAt = "2025-01-01T00:00:00Z",
        });

        var freshJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45));
        var refresher = new FakeTokenRefresher().EnqueueSuccess(freshJwt);

        var msalReads = 0;
        var provider = CreateProvider(
            msalCacheJson: "should-not-be-read",
            clock: () => now,
            refresher: refresher,
            msalReadCounter: r => msalReads = r);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe(freshJwt);
        msalReads.ShouldBe(0); // The whole point of bootstrap-once.
        refresher.Calls[0].RefreshToken.ShouldBe("stored-rt");
        refresher.Calls[0].TenantId.ShouldBe("tenant-stored");
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidGrantWithStoredEntry_WipesAndReBootstraps()
    {
        // Stored refresh token has been revoked at AAD; user has since re-run `az login`.
        // We should drop the stale entry, re-bootstrap from MSAL, and succeed without intervention.
        var now = DateTimeOffset.UtcNow;
        _refreshStore.TryWrite(new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "revoked-rt",
            ClientId = "client-1",
            TenantId = "tenant-stored",
            AuthorityHost = "login.microsoftonline.com",
        });

        var msalJson = JwtTestFactory.BuildMsalCacheJsonWithRefreshToken(
            refreshTokenSecret: "fresh-rt-from-msal",
            tenantId: "tenant-fresh");
        var newJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45), tenantId: "tenant-fresh");

        var refresher = new FakeTokenRefresher()
            .EnqueueInvalidGrant() // first call: stored token rejected
            .EnqueueSuccess(newJwt); // second call: re-bootstrapped token works

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now, refresher: refresher);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe(newJwt);
        refresher.Calls.Count.ShouldBe(2);
        refresher.Calls[0].RefreshToken.ShouldBe("revoked-rt");
        refresher.Calls[1].RefreshToken.ShouldBe("fresh-rt-from-msal");

        // Store should now hold the fresh entry, not the revoked one.
        var entry = _refreshStore.TryRead().ShouldNotBeNull();
        entry.RefreshToken.ShouldBe("fresh-rt-from-msal");
        entry.TenantId.ShouldBe("tenant-fresh");
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidGrantAndNoMsal_ThrowsWithGuidance()
    {
        // Stored token revoked AND no MSAL fallback available — caller must intervene.
        _refreshStore.TryWrite(new TwigRefreshTokenStoreEntry
        {
            RefreshToken = "revoked-rt",
            ClientId = "client-1",
            TenantId = "tenant-stored",
            AuthorityHost = "login.microsoftonline.com",
        });

        var refresher = new FakeTokenRefresher().EnqueueInvalidGrant();
        var provider = CreateProvider(msalCacheJson: null, refresher: refresher);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("az login");
        ex.Message.ShouldContain("twig auth clear");

        // The revoked entry should have been wiped during the failed re-bootstrap attempt.
        _refreshStore.Exists().ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefresherReturnsWrongAudienceJwt_FallsThrough()
    {
        // Defensive: even though we ask for ADO scope, if the refresher somehow returns
        // a wrong-audience JWT we must reject it (and not poison the file cache).
        var now = DateTimeOffset.UtcNow;
        var msalJson = JwtTestFactory.BuildMsalCacheJsonWithRefreshToken();
        var wrongAudJwt = JwtTestFactory.BuildWrongAudience(expiresAt: now.AddMinutes(45));
        var refresher = new FakeTokenRefresher().EnqueueSuccess(wrongAudJwt);

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now, refresher: refresher);

        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());

        File.Exists(_fileCache.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_BootstrapSucceedsButRefreshFails_Throws()
    {
        // MSAL bootstrap context is found, but the AAD HTTP exchange fails (network/error,
        // not invalid_grant). Provider should not retry — that path only triggers for
        // invalid_grant on a pre-existing stored entry.
        var msalJson = JwtTestFactory.BuildMsalCacheJsonWithRefreshToken();
        var refresher = new FakeTokenRefresher().EnqueueFailure();

        var provider = CreateProvider(msalCacheJson: msalJson, refresher: refresher);

        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());

        refresher.Calls.Count.ShouldBe(1); // No retry on plain failure.
    }

    #endregion

    #region General behavior

    [Fact]
    public async Task GetAccessTokenAsync_NoCachesAvailable_ThrowsWithGuidance()
    {
        var provider = CreateProvider(msalCacheJson: null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("az login");
        ex.Message.ShouldContain("499b84ac-1321-427f-aa17-267ca6975798");
    }

    [Fact]
    public async Task GetAccessTokenAsync_InMemoryCache_DoesNotReReadFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45));
        _fileCache.TryWrite(jwt, now.AddMinutes(45));

        var msalReads = 0;
        var provider = CreateProvider(
            msalCacheJson: null,
            clock: () => now,
            msalReadCounter: r => msalReads = r);

        var token1 = await provider.GetAccessTokenAsync();
        var token2 = await provider.GetAccessTokenAsync();
        var token3 = await provider.GetAccessTokenAsync();

        token1.ShouldBe(jwt);
        token2.ShouldBe(jwt);
        token3.ShouldBe(jwt);
        msalReads.ShouldBe(0); // File cache hit short-circuits everything.
    }

    [Fact]
    public async Task InvalidateToken_ClearsInMemoryAndFile()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45));
        _fileCache.TryWrite(jwt, now.AddMinutes(45));
        var provider = CreateProvider(clock: () => now);

        await provider.GetAccessTokenAsync(); // populate in-memory
        provider.InvalidateToken();

        File.Exists(_fileCache.Path).ShouldBeFalse();

        // Subsequent call has nothing to fall back to → throws.
        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());
    }

    #endregion
}
