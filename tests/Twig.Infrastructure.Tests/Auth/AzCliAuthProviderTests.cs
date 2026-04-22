using System.Diagnostics;
using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Auth;

/// <summary>
/// Tests for <see cref="AzCliAuthProvider"/>.
/// Uses a fake process factory to avoid running real 'az' commands.
/// </summary>
public class AzCliAuthProviderTests : IDisposable
{
    private readonly string _cachePath = Path.Combine(
        Path.GetTempPath(), $"twig-test-cache-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (File.Exists(_cachePath)) File.Delete(_cachePath);
    }

    [Fact]
    public async Task GetAccessTokenAsync_SuccessfulProcess_ReturnsToken()
    {
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("my-test-token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("my-test-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesToken_NoSecondProcessSpawn()
    {
        var callCount = 0;
        var provider = new AzCliAuthProvider(
            psi =>
            {
                callCount++;
                return CreateFakeProcess("cached-token\n", "", exitCode: 0);
            },
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var token1 = await provider.GetAccessTokenAsync();
        var token2 = await provider.GetAccessTokenAsync();

        token1.ShouldBe("cached-token");
        token2.ShouldBe("cached-token");
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ExpiredToken_RefetchesToken()
    {
        var callCount = 0;
        var now = DateTimeOffset.UtcNow;
        var clock = now;

        var provider = new AzCliAuthProvider(
            psi =>
            {
                callCount++;
                return CreateFakeProcess($"token-{callCount}\n", "", exitCode: 0);
            },
            () => clock,
            _cachePath);

        var token1 = await provider.GetAccessTokenAsync();
        token1.ShouldBe("token-1");
        callCount.ShouldBe(1);

        // Advance clock past the 50-minute TTL
        clock = now + TimeSpan.FromMinutes(51);
        var token2 = await provider.GetAccessTokenAsync();
        token2.ShouldBe("token-2");
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_TokenNotExpired_UsesCachedToken()
    {
        var callCount = 0;
        var now = DateTimeOffset.UtcNow;
        var clock = now;

        var provider = new AzCliAuthProvider(
            psi =>
            {
                callCount++;
                return CreateFakeProcess("stable-token\n", "", exitCode: 0);
            },
            () => clock,
            _cachePath);

        await provider.GetAccessTokenAsync();
        callCount.ShouldBe(1);

        // Advance clock less than 50 minutes
        clock = now + TimeSpan.FromMinutes(49);
        var token = await provider.GetAccessTokenAsync();
        token.ShouldBe("stable-token");
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonZeroExit_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("", "ERROR: Not logged in", exitCode: 1),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("az login");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ProcessStartThrows_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(
            psi => throw new System.ComponentModel.Win32Exception("az not found"),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("not installed");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ProcessStartReturnsNull_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(
            psi => null,
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("Failed to start");
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyOutput_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("empty token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_WritesFileCache_AfterSuccessfulFetch()
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("fetched-token\n", "", exitCode: 0),
            () => now,
            _cachePath);

        await provider.GetAccessTokenAsync();

        File.Exists(_cachePath).ShouldBeTrue();
        var lines = File.ReadAllLines(_cachePath);
        lines.Length.ShouldBeGreaterThanOrEqualTo(2);
        lines[1].ShouldBe("fetched-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReadsFileCache_AvoidingProcessSpawn()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now + TimeSpan.FromMinutes(30);
        File.WriteAllText(_cachePath, $"{expiry.UtcTicks}\ncached-file-token\n");

        var callCount = 0;
        var provider = new AzCliAuthProvider(
            psi =>
            {
                callCount++;
                return CreateFakeProcess("should-not-be-used\n", "", exitCode: 0);
            },
            () => now,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("cached-file-token");
        callCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_IgnoresExpiredFileCache()
    {
        var now = DateTimeOffset.UtcNow;
        var expiry = now - TimeSpan.FromMinutes(5); // expired
        File.WriteAllText(_cachePath, $"{expiry.UtcTicks}\nstale-token\n");

        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("fresh-token\n", "", exitCode: 0),
            () => now,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fresh-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_IgnoresCorruptFileCache()
    {
        File.WriteAllText(_cachePath, "not-a-number\ngarbage\n");

        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("recovered-token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("recovered-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_CrossProcess_SecondProviderReadsFirstProvidersCache()
    {
        var now = DateTimeOffset.UtcNow;

        // First "process" fetches and writes cache
        var provider1 = new AzCliAuthProvider(
            psi => CreateFakeProcess("shared-token\n", "", exitCode: 0),
            () => now,
            _cachePath);
        await provider1.GetAccessTokenAsync();

        // Second "process" should read from file, not spawn az
        var callCount = 0;
        var provider2 = new AzCliAuthProvider(
            psi =>
            {
                callCount++;
                return CreateFakeProcess("should-not-be-used\n", "", exitCode: 0);
            },
            () => now,
            _cachePath);

        var token = await provider2.GetAccessTokenAsync();

        token.ShouldBe("shared-token");
        callCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingCacheFile_FallsThrough()
    {
        // _cachePath doesn't exist yet — provider should fall through to az CLI
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("fallback-token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("fallback-token");
    }

    [Fact]
    public async Task Constructor_ExplicitTimeout_UsesProvidedValue()
    {
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath,
            TimeSpan.FromSeconds(60));

        // The provider should work normally with a custom timeout
        var token = await provider.GetAccessTokenAsync();
        token.ShouldBe("token");
    }

    [Fact]
    public async Task Constructor_NullTimeout_FallsBackToResolveTimeout()
    {
        // When processTimeout is null, should use ResolveTimeout() (env var or default 10s)
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath,
            null);

        var token = await provider.GetAccessTokenAsync();
        token.ShouldBe("token");
    }

    [Fact]
    public async Task TimeoutErrorMessage_ContainsTWIG_AZ_TIMEOUT_Guidance()
    {
        // Use a tiny timeout to force a timeout on a slow fake process
        var provider = new AzCliAuthProvider(
            psi => CreateSlowProcess(),
            () => DateTimeOffset.UtcNow,
            _cachePath,
            TimeSpan.FromMilliseconds(1));

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("TWIG_AZ_TIMEOUT=30");
    }

    [Fact]
    public async Task GetAccessTokenAsync_CustomTimeout_UsesInjectedValue()
    {
        var provider = new AzCliAuthProvider(
            psi => CreateSlowProcess(),
            () => DateTimeOffset.UtcNow,
            _cachePath,
            TimeSpan.FromMilliseconds(1));

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Constructor_3ParamCtor_StillChainsProperly()
    {
        // Existing 3-param constructor should still work (chains to 4-param with null timeout)
        var provider = new AzCliAuthProvider(
            psi => CreateFakeProcess("three-param-token\n", "", exitCode: 0),
            () => DateTimeOffset.UtcNow,
            _cachePath);

        var token = await provider.GetAccessTokenAsync();
        token.ShouldBe("three-param-token");
    }

    /// <summary>
    /// Creates a fake Process that returns predetermined stdout/stderr.
    /// Uses a real process ('dotnet --version' or similar) but overrides streams.
    /// </summary>
    private static Process CreateFakeProcess(string stdout, string stderr, int exitCode)
    {
        // We use a helper script that echoes the expected output
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            // Construct a command that echoes stdout to stdout, stderr to stderr, and exits with code
            var stdoutEscaped = stdout.Trim().Replace("\"", "\\\"");
            var stderrEscaped = stderr.Trim().Replace("\"", "\\\"");

            var cmd = $"/c \"";
            if (!string.IsNullOrEmpty(stdoutEscaped))
                cmd += $"echo {stdoutEscaped}";
            else
                cmd += "echo.>nul";

            if (!string.IsNullOrEmpty(stderrEscaped))
                cmd += $" & echo {stderrEscaped} 1>&2";

            cmd += $" & exit /b {exitCode}\"";
            psi.Arguments = cmd;
        }
        else
        {
            var stdoutEscaped = stdout.Trim().Replace("'", "'\\''");
            var stderrEscaped = stderr.Trim().Replace("'", "'\\''");
            psi.Arguments = $"-c \"printf '%s' '{stdoutEscaped}'; printf '%s' '{stderrEscaped}' >&2; exit {exitCode}\"";
        }

        return Process.Start(psi)!;
    }

    /// <summary>
    /// Creates a process that sleeps long enough to trigger a timeout.
    /// </summary>
    private static Process CreateSlowProcess()
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments = isWindows ? "/c \"ping -n 10 127.0.0.1 >nul\"" : "-c \"sleep 10\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Process.Start(psi)!;
    }
}
