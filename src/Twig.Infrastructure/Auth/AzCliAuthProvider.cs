using System.Diagnostics;
using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Implements <see cref="IAuthenticationProvider"/> via Azure CLI.
/// Runs <c>az account get-access-token</c> and caches the token both in-memory (50-min TTL)
/// and on disk (<c>~/.twig/.token-cache</c>) to avoid cross-process contention when multiple
/// twig CLI invocations run concurrently.
/// </summary>
internal sealed class AzCliAuthProvider : IAuthenticationProvider
{
    private const string AzResource = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);

    private static readonly string DefaultCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".twig", ".token-cache");

    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _cachePath;
    private readonly TimeSpan _processTimeout;
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
        : this(psi => Process.Start(psi), () => DateTimeOffset.UtcNow, DefaultCachePath, null)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with an injectable process factory (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter)
        : this(processStarter, () => DateTimeOffset.UtcNow, DefaultCachePath, null)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with injectable process factory and clock (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter, Func<DateTimeOffset> clock)
        : this(processStarter, clock, DefaultCachePath, null)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with injectable process factory, clock, and cache path (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter, Func<DateTimeOffset> clock, string cachePath)
        : this(processStarter, clock, cachePath, null)
    {
    }

    /// <summary>
    /// Creates an AzCliAuthProvider with all dependencies injectable (for testing).
    /// </summary>
    internal AzCliAuthProvider(Func<ProcessStartInfo, Process?> processStarter, Func<DateTimeOffset> clock, string cachePath, TimeSpan? processTimeout)
    {
        _processStarter = processStarter;
        _clock = clock;
        _cachePath = cachePath;
        _processTimeout = processTimeout ?? ResolveTimeout();
    }

    /// <summary>
    /// Reads the <c>TWIG_AZ_TIMEOUT</c> environment variable and returns a <see cref="TimeSpan"/>
    /// for valid positive integers, otherwise returns <see cref="DefaultProcessTimeout"/>.
    /// </summary>
    private static TimeSpan ResolveTimeout()
    {
        var value = Environment.GetEnvironmentVariable("TWIG_AZ_TIMEOUT");
        if (value is not null
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return DefaultProcessTimeout;
    }

    /// <inheritdoc />
    public void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiry = default;
        TryDeleteFileCache();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // 1. In-memory cache (same process, hot path)
        if (_cachedToken is not null && _clock() < _tokenExpiry)
            return _cachedToken;

        // 2. Cross-process file cache (avoids az CLI lock contention)
        var (fileToken, fileExpiry) = TryReadFileCache();
        if (fileToken is not null && _clock() < fileExpiry)
        {
            _cachedToken = fileToken;
            _tokenExpiry = fileExpiry;
            return fileToken;
        }

        // 3. Token expired or not yet fetched — shell out to az CLI
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
            timeoutCts.CancelAfter(_processTimeout);

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

            // Persist to file cache for other twig processes
            TryWriteFileCache(token, _tokenExpiry);

            return token;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(); } catch (Exception) { /* best effort */ }
            throw new AdoAuthenticationException(
                $"Azure CLI timed out after {_processTimeout.TotalSeconds}s. Set TWIG_AZ_TIMEOUT=30 to increase the timeout.");
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Tries to read a cached token from the cross-process file cache.
    /// Returns (null, default) on any failure — callers fall through to az CLI.
    /// </summary>
    private (string? token, DateTimeOffset expiry) TryReadFileCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return (null, default);

            var lines = File.ReadAllLines(_cachePath);
            if (lines.Length < 2)
                return (null, default);

            if (!long.TryParse(lines[0], NumberStyles.None,
                    CultureInfo.InvariantCulture, out var ticks))
                return (null, default);

            var expiry = new DateTimeOffset(ticks, TimeSpan.Zero);
            var token = lines[1];

            if (string.IsNullOrWhiteSpace(token))
                return (null, default);

            return (token, expiry);
        }
        catch
        {
            // Corrupt file, permission error, etc. — fall through to az CLI
            return (null, default);
        }
    }

    /// <summary>
    /// Writes the token to a cross-process file cache. Uses atomic write (tmp + rename)
    /// so concurrent readers never see a partial file. Best-effort — failures are silently ignored.
    /// </summary>
    private void TryWriteFileCache(string token, DateTimeOffset expiry)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var tmpPath = _cachePath + ".tmp";
            File.WriteAllText(tmpPath,
                $"{expiry.UtcTicks.ToString(CultureInfo.InvariantCulture)}\n{token}\n");
            File.Move(tmpPath, _cachePath, overwrite: true);

            // Restrict permissions on Unix (Windows inherits user-directory ACLs)
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_cachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort — if we can't write the cache, az CLI still works
        }
    }

    /// <summary>
    /// Deletes the cross-process file cache. Best-effort — failures are silently ignored.
    /// </summary>
    private void TryDeleteFileCache()
    {
        try
        {
            if (File.Exists(_cachePath))
                File.Delete(_cachePath);
        }
        catch
        {
            // Best effort
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
