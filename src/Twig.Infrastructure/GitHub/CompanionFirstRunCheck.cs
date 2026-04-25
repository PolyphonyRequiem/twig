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
}
