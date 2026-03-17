namespace Twig.Domain.Interfaces;

/// <summary>
/// Domain record representing a GitHub release asset (binary download).
/// </summary>
public sealed record GitHubReleaseAssetInfo(string Name, string BrowserDownloadUrl, long Size);

/// <summary>
/// Domain record representing a GitHub release.
/// </summary>
public sealed record GitHubReleaseInfo(
    string Tag,
    string Name,
    string Body,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<GitHubReleaseAssetInfo> Assets);

/// <summary>
/// Abstracts access to GitHub Releases API for self-update and changelog features.
/// </summary>
public interface IGitHubReleaseService
{
    /// <summary>
    /// Gets the latest published release, or null if none found.
    /// </summary>
    Task<GitHubReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the most recent <paramref name="count"/> releases.
    /// </summary>
    Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(int count, CancellationToken ct = default);
}
