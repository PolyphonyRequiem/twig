using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="AdoAccessTokenProvider"/>.
/// Covers the audience-validation guard added in response to issue #164.
/// </summary>
public sealed class AdoAccessTokenProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TwigTokenFileCache _fileCache;

    public AdoAccessTokenProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-auth-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fileCache = new TwigTokenFileCache(Path.Combine(_tempDir, ".token-cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private AdoAccessTokenProvider CreateProvider(
        string? msalCacheJson = null,
        Func<DateTimeOffset>? clock = null,
        MsalTokenRefresher? refresher = null)
        => new(
            msalCachePath: Path.Combine(_tempDir, "msal_token_cache.json"),
            msalCacheReader: (_, _) => Task.FromResult(msalCacheJson),
            clock: clock,
            refresher: refresher,
            fileCache: _fileCache);

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

        // The poisoned file cache must have been wiped so subsequent calls don't poison again.
        File.Exists(_fileCache.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccessTokenAsync_FileCacheExpired_FallsThrough()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(1));
        _fileCache.TryWrite(expiredJwt, now.AddMinutes(1)); // Inside the 5-min expiry buffer

        var provider = CreateProvider(msalCacheJson: null, clock: () => now);

        // No MSAL cache, no fallback — must throw.
        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessTokenAsync_MsalCacheValidAdoToken_ReturnsAndPersists()
    {
        var now = DateTimeOffset.UtcNow;
        var jwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(45));
        var msalJson = JwtTestFactory.BuildMsalCacheJson(
            secret: jwt,
            target: JwtTestFactory.AdoResourceId + "/.default",
            expiresOnEpoch: now.AddMinutes(45).ToUnixTimeSeconds());

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe(jwt);
        // Token persisted to file cache for sharing with other twig processes
        File.Exists(_fileCache.Path).ShouldBeTrue();
        var (cachedToken, _) = _fileCache.TryRead();
        cachedToken.ShouldBe(jwt);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MsalCacheTokenWithMatchingTargetButWrongJwtAudience_Skipped()
    {
        // Subtle case: MSAL cache "Target" string contains the ADO resource ID
        // (so the coarse prefilter passes) but the actual JWT's aud claim is different.
        // This was the latent bug class — the prefilter trusted Target alone.
        var now = DateTimeOffset.UtcNow;
        var wrongJwt = JwtTestFactory.BuildWrongAudience(expiresAt: now.AddMinutes(45));
        var msalJson = JwtTestFactory.BuildMsalCacheJson(
            secret: wrongJwt,
            target: JwtTestFactory.AdoResourceId + "/.default", // Looks right
            expiresOnEpoch: now.AddMinutes(45).ToUnixTimeSeconds());

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now);

        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());
    }

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

        var readCount = 0;
        var provider = new AdoAccessTokenProvider(
            msalCachePath: Path.Combine(_tempDir, "msal.json"),
            msalCacheReader: (_, _) => { readCount++; return Task.FromResult<string?>(null); },
            clock: () => now,
            fileCache: _fileCache);

        var token1 = await provider.GetAccessTokenAsync();
        var token2 = await provider.GetAccessTokenAsync();
        var token3 = await provider.GetAccessTokenAsync();

        token1.ShouldBe(jwt);
        token2.ShouldBe(jwt);
        token3.ShouldBe(jwt);
        // First call read file cache (which hit); subsequent calls used in-memory only
        readCount.ShouldBe(0); // MSAL cache reader never invoked because file cache hit
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

        // Subsequent call has nothing to fall back to → throws
        await Should.ThrowAsync<AdoAuthenticationException>(() => provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessTokenAsync_MsalCacheHasMultipleTokens_PicksLongestLivedValid()
    {
        var now = DateTimeOffset.UtcNow;
        var shorterJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(20), upn: "shorter@x.com");
        var longerJwt = JwtTestFactory.Build(expiresAt: now.AddMinutes(50), upn: "longer@x.com");
        var wrongJwt = JwtTestFactory.BuildWrongAudience(expiresAt: now.AddMinutes(60));

        var msalJson = $$"""
        {
            "AccessToken": {
                "e1": { "secret": "{{shorterJwt}}", "target": "{{JwtTestFactory.AdoResourceId}}/.default", "expires_on": "{{now.AddMinutes(20).ToUnixTimeSeconds()}}" },
                "e2": { "secret": "{{longerJwt}}",  "target": "{{JwtTestFactory.AdoResourceId}}/.default", "expires_on": "{{now.AddMinutes(50).ToUnixTimeSeconds()}}" },
                "e3": { "secret": "{{wrongJwt}}",   "target": "{{JwtTestFactory.AdoResourceId}}/.default", "expires_on": "{{now.AddMinutes(60).ToUnixTimeSeconds()}}" }
            }
        }
        """;

        var provider = CreateProvider(msalCacheJson: msalJson, clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        // Longest-lived ADO-audience token wins; the wrong-audience entry is skipped despite expiring later.
        token.ShouldBe(longerJwt);
    }
}
