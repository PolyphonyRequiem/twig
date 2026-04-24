using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed publish [id] [--all] [--force] [--dry-run] [--link-branch]</c>:
/// publishes seeds to Azure DevOps as real work items.
/// With an ID, publishes a single seed.
/// With --all, publishes all seeds in topological order.
/// After publishing, updates active context if it pointed to a published seed.
/// When --link-branch is specified, links published seeds to the given branch.
/// </summary>
public sealed class SeedPublishCommand(
    SeedPublishOrchestrator orchestrator,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    IAdoWorkItemService adoService,
    IAdoGitService? adoGitService = null)
{
    /// <summary>Publish one or all seeds to Azure DevOps.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool all = false,
        bool force = false,
        bool dryRun = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        string? linkBranch = null,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);

        // Upfront branch validation — resolve projectId/repoId once before publish loop
        var artifactUri = await ResolveBranchArtifactUriAsync(linkBranch, dryRun, fmt, ct);

        if (all)
        {
            var batchResult = await orchestrator.PublishAllAsync(force, dryRun, ct);
            Console.WriteLine(fmt.FormatSeedPublishBatchResult(batchResult));

            // Link each created seed to the branch (best-effort)
            if (artifactUri is not null)
            {
                foreach (var r in batchResult.Results)
                    await TryLinkBranchAsync(r, artifactUri, fmt, ct);
            }

            // Update context if the active seed was published
            if (activeId.HasValue && !dryRun)
            {
                var published = batchResult.Results
                    .FirstOrDefault(r => r.OldId == activeId.Value && r.Status == SeedPublishStatus.Created);
                if (published is not null && published.NewId > 0)
                    await contextStore.SetActiveWorkItemIdAsync(published.NewId, ct);
            }

            return batchResult.HasErrors ? 1 : 0;
        }

        if (!id.HasValue)
        {
            Console.Error.WriteLine(fmt.FormatError("Specify a seed ID or use --all."));
            return 1;
        }

        var result = await orchestrator.PublishAsync(id.Value, force, dryRun, ct);
        Console.WriteLine(fmt.FormatSeedPublishResult(result));

        // Link the created seed to the branch (best-effort)
        if (artifactUri is not null)
            await TryLinkBranchAsync(result, artifactUri, fmt, ct);

        // Update context if the published seed was the active item
        if (result.Status == SeedPublishStatus.Created && activeId == id.Value && result.NewId > 0)
            await contextStore.SetActiveWorkItemIdAsync(result.NewId, ct);

        return result.IsSuccess ? 0 : 1;
    }

    /// <summary>
    /// Resolves the branch artifact URI once upfront. Returns null if linking should be skipped
    /// (no linkBranch specified, dry-run, git service unavailable, or IDs unresolvable).
    /// </summary>
    private async Task<string?> ResolveBranchArtifactUriAsync(
        string? linkBranch, bool dryRun, IOutputFormatter fmt, CancellationToken ct)
    {
        if (linkBranch is null || dryRun)
            return null;

        if (adoGitService is null)
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                "--link-branch requires git service configuration. Skipping branch linking."));
            return null;
        }

        var projectId = await adoGitService.GetProjectIdAsync(ct);
        var repoId = await adoGitService.GetRepositoryIdAsync(ct);

        if (projectId is null || repoId is null)
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                "Could not resolve project/repository IDs. Skipping branch linking."));
            return null;
        }

        var encodedBranch = Uri.EscapeDataString(linkBranch);
        return $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}";
    }

    /// <summary>
    /// Attempts to link a published seed to the branch. Best-effort: logs warnings on failure.
    /// Only links seeds with <see cref="SeedPublishStatus.Created"/> status and a valid NewId.
    /// </summary>
    private async Task TryLinkBranchAsync(
        SeedPublishResult result, string artifactUri, IOutputFormatter fmt, CancellationToken ct)
    {
        if (result.Status != SeedPublishStatus.Created || result.NewId <= 0)
            return;

        try
        {
            var remote = await adoService.FetchAsync(result.NewId, ct);
            await adoGitService!.AddArtifactLinkAsync(
                result.NewId, artifactUri, "ArtifactLink", remote.Revision, "Branch", ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Failed to link branch to #{result.NewId}: {ex.Message}"));
        }
    }
}
