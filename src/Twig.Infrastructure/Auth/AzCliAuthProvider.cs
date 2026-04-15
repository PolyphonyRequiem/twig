using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Implements <see cref="IAuthenticationProvider"/> via Azure CLI.
/// Runs <c>az account get-access-token</c> and caches the token in-memory with a 50-minute TTL.
/// </summary>
internal sealed class AzCliAuthProvider : IAuthenticationProvider
{
    private const string AzResource = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);

    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<DateTimeOffset> _clock;
    // Note: _cachedToken/_tokenExpiry are intentionally not thread-safe.
    // AzCliAuthProvider is designed for single-threaded CLI usage where only one
    // logical caller invokes GetAccessTokenAsync at a time. If this provider is
    // ever used from a concurrent context, guard the refresh block with a SemaphoreSlim.
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;

    /// <summary>
    /// Creates an AzCliAuthProvider that uses <see cref="Process.Start"/> by default.
    /// </summary>
    public AzCliAuthProvider()
        : this(psi => Process.Start(psi), () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with an injectable process factory (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter)
        : this(processStarter, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with injectable process factory and clock (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter, Func<DateTimeOffset> clock)
    {
        _processStarter = processStarter;
        _clock = clock;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && _clock() < _tokenExpiry)
            return _cachedToken;

        // Token expired or not yet fetched — clear and re-fetch
        _cachedToken = null;

        var azPath = ResolveAzPath();
        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            Arguments = $"account get-access-token --resource {AzResource} --query accessToken -o tsv",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = _processStarter(psi);
        }
        catch (Exception ex)
        {
            throw new AdoAuthenticationException(
                $"Azure CLI (az) is not installed or not found in PATH. Install from https://aka.ms/install-azure-cli. Details: {ex.Message}");
        }

        if (process is null)
        {
            throw new AdoAuthenticationException(
                "Failed to start Azure CLI process. Ensure 'az' is installed and in PATH.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeout);

            // Read both streams concurrently to avoid deadlock if the process fills
            // one pipe buffer while we're blocked reading the other.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                throw new AdoAuthenticationException(
                    $"Azure CLI returned exit code {process.ExitCode}. Run 'az login' to authenticate. {stderr.Trim()}");
            }

            var token = stdout.Trim();
            if (string.IsNullOrEmpty(token))
            {
                throw new AdoAuthenticationException(
                    "Azure CLI returned an empty token. Run 'az login' to authenticate.");
            }

            _cachedToken = token;
            _tokenExpiry = _clock() + TokenTtl;
            return token;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(); } catch (Exception) { /* best effort */ }
            throw new AdoAuthenticationException(
                $"Azure CLI timed out after {ProcessTimeout.TotalSeconds}s. Ensure 'az' is responsive.");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Resolves the Azure CLI executable path. On Windows, searches PATH for
    /// <c>az.cmd</c> to avoid needing <c>cmd.exe /c</c>. On Unix, returns <c>az</c>.
    /// </summary>
    private static string ResolveAzPath()
    {
        if (!OperatingSystem.IsWindows())
            return "az";

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, "az.cmd");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Fallback: let the OS resolve it
        return "az.cmd";
    }
}
