using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.GitHub;

/// <summary>
/// On first run after upgrade, detects missing companion binaries and installs them
/// from the matching GitHub release archive. Writes a version marker so each version
/// is only attempted once — users must run <c>twig upgrade</c> to retry.
/// </summary>
internal sealed class CompanionFirstRunCheck(
    IGitHubReleaseService releaseService,
    ICompanionInstaller companionInstaller,
    IFileSystem fileSystem)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ensures all companion tools are present alongside the main <c>twig</c> binary.
    /// Fast path: returns immediately when all companions exist (zero I/O writes).
    /// Slow path: downloads missing companions from the matching release archive.
    /// </summary>
    internal async Task EnsureCompanionsAsync(
        string? processPath,
        string currentVersion,
        CancellationToken ct = default)
    {
        // Phase 1 — Fast path: no I/O write
        if (processPath is null)
            return;

        // TICKET-0311: the check is only meaningful when the RUNNING PROCESS IS the
        // twig binary, because "the install dir" is defined as the directory holding
        // that binary. Under a framework-dependent launch (`dotnet path/to/twig.dll`)
        // Environment.ProcessPath is the *dotnet host*, so `dir` resolves to the SDK
        // folder — where companions can never be present and never should be written.
        //
        // The old code therefore took the slow path on EVERY `dotnet twig.dll` run: it
        // made a blocking GitHub call with a 60 s budget and dropped a `.twig-version`
        // marker into the SDK directory. In the Cli suite, which spawns the CLI exactly
        // that way, a single slow GitHub response blew the 300 s vstest run timeout and
        // aborted the host — non-deterministically, because on a healthy network the
        // call usually returned in milliseconds. "Usually fast" was the bug.
        //
        // Gating on the process identity makes the hang structurally impossible rather
        // than merely unlikely: the network path is now unreachable from a dotnet-hosted
        // run, so no fixture has to remember to force the offline branch.
        if (!IsTwigHostProcess(processPath))
            return;

        var dir = Path.GetDirectoryName(processPath);
        if (dir is null)
            return;

        var versionFile = Path.Combine(dir, ".twig-version");

        var missingCompanions = CompanionTools.All
            .Select(CompanionTools.GetExeName)
            .Where(exe => !fileSystem.FileExists(Path.Combine(dir, exe)))
            .ToList();

        if (missingCompanions.Count == 0)
            return;

        // Phase 2 — Version marker check
        if (fileSystem.FileExists(versionFile))
        {
            using var stream = fileSystem.FileOpenRead(versionFile);
            using var reader = new StreamReader(stream);
            var storedVersion = (await reader.ReadToEndAsync(ct)).Trim();
            if (storedVersion == currentVersion)
                return;
        }

        // Phase 3 — Download with timeout
        try
        {
            await Console.Error.WriteLineAsync("Installing companion tools...");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DownloadTimeout);

            var rid = PlatformHelper.DetectRid()
                ?? throw new InvalidOperationException("Unable to detect platform RID.");

            var release = await releaseService.GetReleaseByTagAsync($"v{currentVersion}", cts.Token)
                ?? throw new InvalidOperationException($"No GitHub release found for tag v{currentVersion}.");

            var (asset, archiveName) = PlatformHelper.FindAsset(release, rid);
            if (asset is null)
                throw new InvalidOperationException($"No release asset found for {archiveName}.");

            var results = await companionInstaller.InstallCompanionsOnlyAsync(
                asset.BrowserDownloadUrl, archiveName, missingCompanions, dir, cts.Token);

            var installed = results.Count(r => r.Found);
            await Console.Error.WriteLineAsync($"  {installed}/{missingCompanions.Count} companion(s) installed.");
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                "  Companion installation timed out. Run 'twig upgrade' to install manually.");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"  Companion installation failed: {ex.Message}");
            await Console.Error.WriteLineAsync("  Run 'twig upgrade' to install manually.");
        }

        // Phase 4 — Write version marker (always, after download attempt)
        using var markerStream = fileSystem.FileCreate(versionFile);
        using var writer = new StreamWriter(markerStream);
        writer.Write(currentVersion);
    }

    /// <summary>
    /// True when <paramref name="processPath"/> is the twig executable itself, rather than
    /// a generic host (<c>dotnet</c>) running <c>twig.dll</c>.
    /// </summary>
    /// <remarks>
    /// TICKET-0311. Companion binaries live next to the twig executable, so the whole
    /// notion of "companions are missing from my install dir" is only defined for a
    /// self-contained/apphost launch. Under <c>dotnet twig.dll</c> the process path names
    /// the SDK host, and treating that directory as an install dir made the check both
    /// useless (companions can never be there) and harmful (a blocking 60 s GitHub call on
    /// every such run, plus a stray marker file written into the SDK folder).
    /// </remarks>
    internal static bool IsTwigHostProcess(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "twig", StringComparison.OrdinalIgnoreCase);
    }
}
