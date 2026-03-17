using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Abstracts ADO Git REST API operations (PRs, artifact links).
/// Injected as <c>IAdoGitService?</c> — null when not configured.
/// </summary>
public interface IAdoGitService
{
    Task AddArtifactLinkAsync(int workItemId, string artifactUri, string linkType, int revision, CancellationToken ct = default);
    Task<PullRequestInfo> CreatePullRequestAsync(PullRequestCreate request, CancellationToken ct = default);

    /// <summary>
    /// Returns active PRs whose source branch matches <paramref name="branchName"/>.
    /// <paramref name="branchName"/> should be a short branch name (e.g. "feature/123-test"),
    /// not a full ref — the implementation prepends "refs/heads/" internally.
    /// </summary>
    Task<IReadOnlyList<PullRequestInfo>> GetPullRequestsForBranchAsync(string branchName, CancellationToken ct = default);
    Task<string?> GetRepositoryIdAsync(CancellationToken ct = default);
    Task<string?> GetProjectIdAsync(CancellationToken ct = default);
}
