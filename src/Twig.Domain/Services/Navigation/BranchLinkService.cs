using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Encapsulates branch-to-ADO artifact link logic: resolves the git project/repository
/// IDs, builds the <c>vstfs:///Git/Ref/{projectId}/{repoId}/GB{branchName}</c> URI,
/// and adds it as an artifact link to the specified work item.
/// </summary>
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

        string? projectId;
        string? repoId;
        try
        {
            projectId = await adoGitService.GetProjectIdAsync(ct);
            repoId = await adoGitService.GetRepositoryIdAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BranchLinkResult.GitContextUnavailable(
                workItemId, branchName, $"Failed to resolve git context: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(repoId))
        {
            return new BranchLinkResult.GitContextUnavailable(
                workItemId, branchName, "Git project ID or repository ID could not be resolved.");
        }

        var artifactUri = BuildArtifactUri(projectId, repoId, branchName);

        try
        {
            var alreadyExisted = await adoWorkItemService.AddArtifactLinkAsync(
                workItemId, artifactUri, "Branch", ct);

            return alreadyExisted
                ? new BranchLinkResult.AlreadyLinked(workItemId, branchName, artifactUri)
                : new BranchLinkResult.Linked(workItemId, branchName, artifactUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BranchLinkResult.Failed(
                workItemId, branchName, artifactUri, $"Failed to add artifact link: {ex.Message}");
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
