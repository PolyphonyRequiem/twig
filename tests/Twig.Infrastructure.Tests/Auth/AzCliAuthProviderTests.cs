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
public class AzCliAuthProviderTests
{
    [Fact]
    public async Task GetAccessTokenAsync_SuccessfulProcess_ReturnsToken()
    {
        var provider = new AzCliAuthProvider(psi => CreateFakeProcess("my-test-token\n", "", exitCode: 0));

        var token = await provider.GetAccessTokenAsync();

        token.ShouldBe("my-test-token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_CachesToken_NoSecondProcessSpawn()
    {
        var callCount = 0;
        var provider = new AzCliAuthProvider(psi =>
        {
            callCount++;
            return CreateFakeProcess("cached-token\n", "", exitCode: 0);
        });

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
            () => clock);

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
            () => clock);

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
        var provider = new AzCliAuthProvider(psi => CreateFakeProcess("", "ERROR: Not logged in", exitCode: 1));

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("az login");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ProcessStartThrows_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(psi => throw new System.ComponentModel.Win32Exception("az not found"));

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("not installed");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ProcessStartReturnsNull_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(psi => null);

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("Failed to start");
    }

    [Fact]
    public async Task GetAccessTokenAsync_EmptyOutput_ThrowsAuthException()
    {
        var provider = new AzCliAuthProvider(psi => CreateFakeProcess("", "", exitCode: 0));

        var ex = await Should.ThrowAsync<AdoAuthenticationException>(
            () => provider.GetAccessTokenAsync());

        ex.Message.ShouldContain("empty token");
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
}
