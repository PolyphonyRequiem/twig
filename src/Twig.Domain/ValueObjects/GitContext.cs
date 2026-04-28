using Twig.Domain.ValueObjects;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Best-effort git context enrichment: current branch name and linked pull requests.
/// All fields are optional/empty — failures during detection are swallowed.
/// </summary>
public sealed record GitContext(
    string? CurrentBranch,
    IReadOnlyList<PullRequestInfo> LinkedPullRequests)
{
    /// <summary>Empty context when git enrichment is unavailable or disabled.</summary>
    public static GitContext Empty { get; } = new(null, []);

    /// <summary>True when at least one piece of git context is available.</summary>
    public bool HasData => CurrentBranch is not null || LinkedPullRequests.Count > 0;
}
