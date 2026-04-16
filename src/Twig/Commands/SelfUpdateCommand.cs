using Twig.Domain.Interfaces;
using Twig.Infrastructure.GitHub;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig upgrade</c>: checks GitHub Releases for a newer version and applies the update.
/// </summary>
public sealed class SelfUpdateCommand(
    IGitHubReleaseService releaseService,
    SelfUpdater selfUpdater)
{
    /// <summary>Check for and apply updates from GitHub Releases.</summary>
    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        var currentVersion = VersionHelper.GetVersion();
        Console.WriteLine($"Current version: {currentVersion}");
        Console.WriteLine("Checking for updates...");

        GitHubReleaseInfo? latest;
        try
        {
            latest = await releaseService.GetLatestReleaseAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: Failed to check for updates: {ex.Message}");
            return 1;
        }

        if (latest is null)
        {
            Console.WriteLine("No releases found.");
            return 0;
        }

        var latestTag = latest.Tag;
        var comparison = SemVerComparer.Compare(currentVersion, latestTag);

        if (comparison >= 0)
        {
            Console.WriteLine($"Already up to date ({latestTag})");
            return 0;
        }

        Console.WriteLine($"New version available: {latestTag}");

        // Determine platform RID and find matching asset
        var rid = PlatformHelper.DetectRid();
        if (rid is null)
        {
            Console.Error.WriteLine("error: Could not determine platform. Manual download required.");
            return 1;
        }

        var (asset, archiveName) = PlatformHelper.FindAsset(latest, rid);
        if (asset is null)
        {
            Console.Error.WriteLine($"error: No binary found for platform '{rid}'. Manual download required.");
            return 1;
        }

        Console.WriteLine($"Downloading {archiveName} ({asset.Size / 1024}KB)...");

        try
        {
            var companionExeNames = CompanionTools.All
                .Select(CompanionTools.GetExeName)
                .ToArray();
            var updateResult = await selfUpdater.UpdateBinaryAsync(asset.BrowserDownloadUrl, archiveName, companionExeNames, ct);
            Console.WriteLine();

            // Display changelog
            if (!string.IsNullOrWhiteSpace(latest.Body))
            {
                Console.WriteLine("Release notes:");
                Console.WriteLine(latest.Body);
                Console.WriteLine();
            }

            Console.WriteLine($"Update complete. Restart to use {latestTag}.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: Update failed: {ex.Message}");
            return 1;
        }

        return 0;
    }
}

/// <summary>
/// Minimal AOT-safe SemVer comparison. Handles:
/// <list type="bullet">
///   <item><c>v</c> prefix stripping</item>
///   <item>Numeric <c>major.minor.patch</c> comparison via <see cref="Version"/></item>
///   <item>Pre-release suffix: a version WITH a pre-release (e.g., <c>1.0.1-alpha.0.3</c>)
///         is LESS THAN the same numeric version WITHOUT one (per SemVer §11)</item>
/// </list>
/// </summary>
internal static class SemVerComparer
{
    /// <summary>
    /// Compares two version strings.
    /// Returns negative if <paramref name="a"/> &lt; <paramref name="b"/>,
    /// zero if equal, positive if <paramref name="a"/> &gt; <paramref name="b"/>.
    /// </summary>
    internal static int Compare(string a, string b)
    {
        var (numA, preA) = Parse(a);
        var (numB, preB) = Parse(b);

        var numCmp = numA.CompareTo(numB);
        if (numCmp != 0) return numCmp;

        // If numeric parts are equal: pre-release < release (SemVer §11)
        var hasPreA = !string.IsNullOrEmpty(preA);
        var hasPreB = !string.IsNullOrEmpty(preB);

        if (hasPreA && !hasPreB) return -1; // a is pre-release, b is release → a < b
        if (!hasPreA && hasPreB) return 1;  // a is release, b is pre-release → a > b
        return 0; // both release or both pre-release — treat as equal
    }

    private static (Version numeric, string? preRelease) Parse(string version)
    {
        var v = version.AsSpan();
        if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V'))
            v = v[1..];

        var str = v.ToString();
        var dashIndex = str.IndexOf('-');
        string numericPart;
        string? preRelease;

        if (dashIndex >= 0)
        {
            numericPart = str[..dashIndex];
            preRelease = str[(dashIndex + 1)..];
        }
        else
        {
            numericPart = str;
            preRelease = null;
        }

        return (Version.Parse(numericPart), preRelease);
    }
}
