using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Encapsulates branch-to-ADO artifact link logic: resolves the git project/repository
/// IDs, builds the <c>vstfs:///Git/Ref/{projectId}/{repoId}/GB{branchName}</c> URI,
/// and adds it as an artifact link to the specified work item.
/// </summary>
/// <remarks>
/// Extracted from <see cref="Twig.Domain.Services.BranchNamingService"/>-adjacent
/// logic in <c>BranchCommand</c> to enable reuse by <c>LinkBranchCommand</c>,
/// the <c>BranchCommand</c> refactor, and the MCP <c>twig_link_branch</c> tool.
/// </remarks>
public sealed class BranchLinkService(
    IAdoGitService adoGitService,
    IAdoWorkItemService adoWorkItemService)
{
    /// <summary>
    /// Links the specified branch to a work item as an ADO artifact link.
    /// </summary>
    /// <param name="workItemId">The work item ID to attach the link to.</param>
    /// <param name="branchName">
    /// Short branch name (e.g. "feature/123-fix-login"), not a full ref.
    /// Will be URI-encoded for the vstfs artifact URI.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BranchLinkResult"/> describing the outcome.</returns>
    public async Task<BranchLinkResult> LinkBranchAsync(
        int workItemId,
        string branchName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // 1. Resolve git project and repository IDs
        string? projectId;
        string? repoId;
        try
        {
            projectId = await adoGitService.GetProjectIdAsync(ct);
            repoId = await adoGitService.GetRepositoryIdAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BranchLinkResult
            {
                Status = BranchLinkStatus.GitContextUnavailable,
                WorkItemId = workItemId,
                BranchName = branchName,
                ErrorMessage = $"Failed to resolve git context: {ex.Message}",
            };
        }

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(repoId))
        {
            return new BranchLinkResult
            {
                Status = BranchLinkStatus.GitContextUnavailable,
                WorkItemId = workItemId,
                BranchName = branchName,
                ErrorMessage = "Git project ID or repository ID could not be resolved.",
            };
        }

        // 2. Build vstfs artifact URI
        var artifactUri = BuildArtifactUri(projectId, repoId, branchName);

        // 3. Add artifact link to work item
        try
        {
            var alreadyExisted = await adoWorkItemService.AddArtifactLinkAsync(
                workItemId, artifactUri, "Branch", ct);

            return new BranchLinkResult
            {
                Status = alreadyExisted ? BranchLinkStatus.AlreadyLinked : BranchLinkStatus.Linked,
                WorkItemId = workItemId,
                BranchName = branchName,
                ArtifactUri = artifactUri,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BranchLinkResult
            {
                Status = BranchLinkStatus.Failed,
                WorkItemId = workItemId,
                BranchName = branchName,
                ArtifactUri = artifactUri,
                ErrorMessage = $"Failed to add artifact link: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Builds the vstfs artifact URI for a branch reference.
    /// Format: <c>vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranchName}</c>
    /// </summary>
    internal static string BuildArtifactUri(string projectId, string repoId, string branchName)
    {
        var encodedBranch = Uri.EscapeDataString(branchName);
        return $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}";
    }
}
