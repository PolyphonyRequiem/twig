using System.Diagnostics;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Result of probing a single file for a write-lock.
/// </summary>
/// <param name="Path">Absolute path that was probed.</param>
/// <param name="Exists">True when the file exists on disk at probe time.</param>
/// <param name="IsLocked">True when another process holds the file open in a way that
/// prevents exclusive access (i.e. a Windows-style sharing violation).</param>
/// <param name="HoldingProcessIds">Best-effort list of PIDs whose main module path matches
/// the probed file. Empty on platforms where enumeration is not supported, or when no
/// holders could be identified despite the lock.</param>
internal sealed record FileLockProbeResult(
    string Path,
    bool Exists,
    bool IsLocked,
    IReadOnlyList<int> HoldingProcessIds);

/// <summary>
/// Cross-process file-lock probe used by <see cref="SelfUpdater"/> to fail fast before
/// downloading an update when a peer binary (e.g. <c>twig-mcp.exe</c>) is held open by
/// another process. Exposes a Windows-focused PID enumerator so callers can either
/// surface the offending PIDs or terminate them with <c>--force</c>.
/// </summary>
internal static class FileLockProbe
{
    /// <summary>
    /// Tries to open the file with exclusive sharing. A sharing violation means another
    /// process has an open handle. Missing files are reported as <see cref="FileLockProbeResult.Exists"/>=false
    /// so callers can distinguish "no peer to overwrite" from "peer is locked".
    /// </summary>
    public static FileLockProbeResult Probe(string path)
    {
        if (!File.Exists(path))
        {
            return new FileLockProbeResult(path, Exists: false, IsLocked: false, HoldingProcessIds: []);
        }

        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return new FileLockProbeResult(path, Exists: true, IsLocked: false, HoldingProcessIds: []);
        }
        catch (IOException)
        {
            // Sharing violation or similar — file is locked by someone else.
            var holders = EnumerateHolders(path);
            return new FileLockProbeResult(path, Exists: true, IsLocked: true, HoldingProcessIds: holders);
        }
        catch (UnauthorizedAccessException)
        {
            // ACL denied — surface as locked so callers don't trip on the rename later.
            var holders = EnumerateHolders(path);
            return new FileLockProbeResult(path, Exists: true, IsLocked: true, HoldingProcessIds: holders);
        }
    }

    /// <summary>
    /// Probes a batch of paths and returns results in input order.
    /// </summary>
    public static IReadOnlyList<FileLockProbeResult> ProbeAll(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return paths.Select(Probe).ToList();
    }

    /// <summary>
    /// Best-effort PID enumeration: lists processes whose main module path equals
    /// <paramref name="targetPath"/> (case-insensitive on Windows, case-sensitive elsewhere).
    /// Returns an empty list when enumeration fails or yields no matches.
    /// </summary>
    public static IReadOnlyList<int> EnumerateHolders(string targetPath)
    {
        var resolved = SafeFullPath(targetPath);
        var matches = new List<int>();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return matches;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var process in processes)
        {
            try
            {
                string? modulePath = null;
                try
                {
                    modulePath = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied for system / other-user processes — skip.
                }

                if (modulePath is null) continue;
                if (string.Equals(SafeFullPath(modulePath), resolved, comparison))
                {
                    matches.Add(process.Id);
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return matches;
    }

    /// <summary>
    /// Attempts to terminate every process holding <paramref name="path"/>. Returns the list
    /// of PIDs that were sent a kill signal. Best-effort — failures are swallowed so that
    /// the caller can re-probe and decide how to proceed.
    /// </summary>
    public static IReadOnlyList<int> KillHolders(string path)
    {
        var holders = EnumerateHolders(path);
        var killed = new List<int>(holders.Count);
        foreach (var pid in holders)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: false);
                try { process.WaitForExit(2_000); } catch { }
                killed.Add(pid);
            }
            catch
            {
                // Process may have exited between enumeration and kill — ignore.
            }
        }
        return killed;
    }

    /// <summary>
    /// Removes a stale <c>.tmp</c> sibling that a previous failed update left behind.
    /// Safe to call when the file does not exist.
    /// </summary>
    public static void TryRemoveStaleTemp(string targetPath)
    {
        var tempPath = targetPath + ".tmp";
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort — the next install attempt will overwrite via FileCopy.
        }
    }

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
