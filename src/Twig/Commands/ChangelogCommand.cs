using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig changelog</c>: displays recent release notes from GitHub Releases.
/// </summary>
public sealed class ChangelogCommand(IGitHubReleaseService releaseService)
{
    /// <summary>Display recent release notes.</summary>
    public async Task<int> ExecuteAsync(int count = 5, CancellationToken ct = default)
    {
        if (count < 1)
        {
            Console.Error.WriteLine("error: count must be at least 1.");
            return 1;
        }

        if (count > 100)
            count = 100;

        IReadOnlyList<GitHubReleaseInfo> releases;
        try
        {
            releases = await releaseService.GetReleasesAsync(count, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: Failed to fetch releases: {ex.Message}");
            return 1;
        }

        if (releases.Count == 0)
        {
            Console.WriteLine("No releases found.");
            return 0;
        }

        for (var i = 0; i < releases.Count; i++)
        {
            if (i > 0)
                Console.WriteLine();

            var release = releases[i];
            var date = release.PublishedAt?.ToString("yyyy-MM-dd") ?? "unknown date";
            Console.WriteLine($"## {release.Tag} ({date})");
            Console.WriteLine();

            if (!string.IsNullOrWhiteSpace(release.Body))
                Console.WriteLine(release.Body.TrimEnd());
            else
                Console.WriteLine("No release notes.");
        }

        return 0;
    }
}
