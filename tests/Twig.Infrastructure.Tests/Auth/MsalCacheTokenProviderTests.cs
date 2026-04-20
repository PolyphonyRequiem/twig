using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="MsalCacheTokenProvider"/>.
/// Uses injectable file reader and clock for deterministic testing.
/// </summary>
public class MsalCacheTokenProviderTests
{
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    /// <summary>
    /// Creates a valid MSAL cache JSON with one ADO token.
    /// </summary>
    private static string CreateCacheJson(string secret, string target, long expiresOnEpoch)
    {
        return $$"""
        {
            "AccessToken": {
                "entry1": {
                    "secret": "{{secret}}",
                    "target": "{{target}}",
                    "expires_on": "{{expiresOnEpoch}}"
                }
            }
        }
        """;
    }

    /// <summary>
    /// Creates a valid MSAL cache JSON with multiple token entries.
    /// </summary>
    private static string CreateMultiEntryCacheJson(params (string secret, string target, long expiresOn)[] entries)
    {
        var entryLines = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var (secret, target, expiresOn) = entries[i];
            entryLines.Add($$"""
                "entry{{i}}": {
                    "secret": "{{secret}}",
                    "target": "{{target}}",
                    "expires_on": "{{expiresOn}}"
                }
            """);
        }

        return $$"""
        {
            "AccessToken": {
                {{string.Join(",\n            ", entryLines)}}
            }
        }
        """;
    }

    private static FakeInnerProvider CreateInnerProvider(string token = "inner-fallback-token")
        => new(token);

    [Fact]
    public async Task GetAccessTokenAsync_ValidCachedToken_ReturnsCachedToken()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("msal-token", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("msal-token");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_InMemoryCache_DoesNotRereadFile()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("cached-msal", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider();
        var fileReadCount = 0;

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) =>
            {
                fileReadCount++;
                return Task.FromResult<string?>(json);
            },
            clock: () => now);

        var token1 = await provider.GetAccessTokenAsync();
        var token2 = await provider.GetAccessTokenAsync();

        token1.ShouldBe("cached-msal");
        token2.ShouldBe("cached-msal");
        fileReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ExpiredInMemoryCache_RereadsFile()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var expiresOn = now.AddHours(2).ToUnixTimeSeconds();
        var json = CreateCacheJson("msal-token", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider();
        var fileReadCount = 0;

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) =>
            {
                fileReadCount++;
                return Task.FromResult<string?>(json);
            },
            clock: () => clock);

        await provider.GetAccessTokenAsync();
        fileReadCount.ShouldBe(1);

        // Advance past 50-minute TTL
        clock = now + TimeSpan.FromMinutes(51);

        await provider.GetAccessTokenAsync();
        fileReadCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenExpiringWithinFiveMinutes_FallsToInner()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(4).ToUnixTimeSeconds(); // expires in < 5 min
        var json = CreateCacheJson("almost-expired", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider("fresh-inner-token");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fresh-inner-token");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoAdoTarget_FallsToInner()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("non-ado-token", "https://graph.microsoft.com/.default", expiresOn);
        var inner = CreateInnerProvider("inner-token");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("inner-token");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NullFileContent_FallsToInner()
    {
        var inner = CreateInnerProvider("fallback");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(null),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidJson_FallsToInner()
    {
        var inner = CreateInnerProvider("fallback-json-error");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>("not valid json {{{"),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-json-error");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_FileReaderThrows_FallsToInner()
    {
        var inner = CreateInnerProvider("fallback-io-error");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => throw new IOException("disk error"),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-io-error");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_InvalidExpiresOn_SkipsEntry_FallsToInner()
    {
        var json = """
        {
            "AccessToken": {
                "entry1": {
                    "secret": "bad-expiry-token",
                    "target": "499b84ac-1321-427f-aa17-267ca6975798",
                    "expires_on": "not-a-number"
                }
            }
        }
        """;
        var inner = CreateInnerProvider("fallback-parse-error");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-parse-error");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MultipleEntries_SelectsLongestLived()
    {
        var now = DateTimeOffset.UtcNow;
        var shortExpiry = now.AddMinutes(10).ToUnixTimeSeconds();
        var longExpiry = now.AddMinutes(60).ToUnixTimeSeconds();

        var json = CreateMultiEntryCacheJson(
            ("short-lived", AdoResourceId, shortExpiry),
            ("long-lived", AdoResourceId, longExpiry));

        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("long-lived");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MixedAdoAndNonAdoEntries_SelectsAdoOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var longerExpiry = now.AddMinutes(60).ToUnixTimeSeconds();

        var json = CreateMultiEntryCacheJson(
            ("graph-token", "https://graph.microsoft.com/.default", longerExpiry),
            ("ado-token", AdoResourceId, expiresOn));

        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("ado-token");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyAccessTokenDict_FallsToInner()
    {
        var json = """{ "AccessToken": {} }""";
        var inner = CreateInnerProvider("fallback-empty");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-empty");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NullSecret_SkipsEntry()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = $$"""
        {
            "AccessToken": {
                "entry1": {
                    "secret": null,
                    "target": "{{AdoResourceId}}",
                    "expires_on": "{{expiresOn}}"
                }
            }
        }
        """;
        var inner = CreateInnerProvider("fallback-null-secret");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-null-secret");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsRawSecret_NotBearerPrefixed()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldNotStartWith("Bearer ");
        token.ShouldBe("eyJ0eXAiOiJKV1QiLCJhbGciOiJSUzI1NiJ9");
    }

    [Fact]
    public async Task GetAccessTokenAsync_NullAccessTokenProperty_FallsToInner()
    {
        var json = """{ "AccessToken": null }""";
        var inner = CreateInnerProvider("fallback-null-prop");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-null-prop");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TargetContainsResourceIdCaseInsensitive_Matches()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("case-token", AdoResourceId.ToUpperInvariant(), expiresOn);
        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("case-token");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_InnerProviderThrows_ExceptionPropagates_SemaphoreReleased()
    {
        var inner = new ThrowingInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(null),
            clock: () => DateTimeOffset.UtcNow);

        // First call — inner throws, exception propagates
        await Should.ThrowAsync<InvalidOperationException>(
            () => provider.GetAccessTokenAsync());

        // Second call — proves semaphore was released (would deadlock otherwise)
        await Should.ThrowAsync<InvalidOperationException>(
            () => provider.GetAccessTokenAsync());

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCalls_SemaphoreSerializesAccess()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("concurrent-token", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider();
        var concurrentReaders = 0;
        var maxConcurrentReaders = 0;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: async (_, ct) =>
            {
                var current = Interlocked.Increment(ref concurrentReaders);
                // Track maximum concurrent readers
                int snapshot;
                do
                {
                    snapshot = Volatile.Read(ref maxConcurrentReaders);
                } while (current > snapshot &&
                         Interlocked.CompareExchange(ref maxConcurrentReaders, current, snapshot) != snapshot);

                // Wait on gate to ensure all callers overlap
                await gate.Task;
                Interlocked.Decrement(ref concurrentReaders);
                return json;
            },
            clock: () => now);

        // Launch 5 concurrent calls
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => provider.GetAccessTokenAsync())
            .ToArray();

        // Let the file reader complete after a short delay to let tasks queue up
        await Task.Delay(100);
        gate.SetResult();

        var tokens = await Task.WhenAll(tasks);

        // All should get the same token
        foreach (var t in tokens)
            t.ShouldBe("concurrent-token");

        // SemaphoreSlim(1,1) ensures at most 1 concurrent file read
        maxConcurrentReaders.ShouldBe(1);
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenExpiredHoursAgo_FallsToInner()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredLongAgo = now.AddHours(-2).ToUnixTimeSeconds();
        var json = CreateCacheJson("stale-token", AdoResourceId, expiredLongAgo);
        var inner = CreateInnerProvider("fresh-from-cli");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fresh-from-cli");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TargetContainsResourceIdAsUrlSubstring_Matches()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var scopeUrl = $"{AdoResourceId}/.default";
        var json = CreateCacheJson("scope-url-token", scopeUrl, expiresOn);
        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("scope-url-token");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingAccessTokenKeyInJson_FallsToInner()
    {
        var json = """{ "RefreshToken": {}, "Account": {} }""";
        var inner = CreateInnerProvider("fallback-no-key");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-no-key");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AllAdoEntriesExpired_FallsToInner()
    {
        var now = DateTimeOffset.UtcNow;
        var expired1 = now.AddMinutes(-10).ToUnixTimeSeconds();
        var expired2 = now.AddMinutes(-5).ToUnixTimeSeconds();

        var json = CreateMultiEntryCacheJson(
            ("expired-a", AdoResourceId, expired1),
            ("expired-b", AdoResourceId, expired2));

        var inner = CreateInnerProvider("all-expired-fallback");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("all-expired-fallback");
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_CancellationAlreadyRequested_ThrowsOperationCanceled()
    {
        var inner = CreateInnerProvider();

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(null),
            clock: () => DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(cts.Token));
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyStringSecret_ReturnsEmptyString()
    {
        var now = DateTimeOffset.UtcNow;
        var expiresOn = now.AddMinutes(30).ToUnixTimeSeconds();
        var json = CreateCacheJson("", AdoResourceId, expiresOn);
        var inner = CreateInnerProvider("inner-token");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => now);

        var token = await provider.GetAccessTokenAsync();

        // Empty string is non-null, so implementation accepts it
        token.ShouldBe("");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyJsonObject_FallsToInner()
    {
        var json = """{}""";
        var inner = CreateInnerProvider("fallback-empty-obj");

        var provider = new MsalCacheTokenProvider(
            inner,
            cacheFilePath: "fake-path",
            fileReader: (_, _) => Task.FromResult<string?>(json),
            clock: () => DateTimeOffset.UtcNow);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-empty-obj");
        inner.CallCount.ShouldBe(1);
    }

    /// <summary>
    /// Fake inner auth provider for test isolation.
    /// </summary>
    private sealed class FakeInnerProvider(string token) : IAuthenticationProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(token);
        }
    }

    /// <summary>
    /// Inner provider that always throws, for testing exception propagation.
    /// </summary>
    private sealed class ThrowingInnerProvider : IAuthenticationProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("az CLI unavailable");
        }
    }
}
