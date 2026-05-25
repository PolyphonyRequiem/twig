using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed publish [id] [--all] [--force] [--dry-run] [--link-branch] [--repo]</c>:
/// publishes seeds to Azure DevOps as real work items.
/// With an ID, publishes a single seed.
/// With --all, publishes all seeds in topological order.
/// After publishing, updates active context if it pointed to a published seed.
/// When --link-branch is specified, links published seeds to the given branch.
/// When --repo is specified, resolves the branch in the named repository instead of the default.
/// </summary>
public sealed class SeedPublishCommand(
    SeedPublishOrchestrator orchestrator,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
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
        string? repoName = null,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);

        // Upfront branch validation — resolve projectId/repoId once before publish loop
        var artifactUri = await ResolveBranchArtifactUriAsync(linkBranch, repoName, dryRun, fmt, ct);

        if (all)
        {
            var batchResult = await orchestrator.PublishAllAsync(force, dryRun, ct);
            RenderBatchResult(batchResult, outputFormat);

            // Link each created seed to the branch (best-effort)
            if (artifactUri is not null)
            {
                var (linked, failed) = await LinkBatchAsync(batchResult.Results, artifactUri, fmt, ct);
                EmitLinkSummary(linked, failed, linkBranch!, fmt);
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
        RenderSingleResult(result, outputFormat);

        // Link the created seed to the branch (best-effort)
        if (artifactUri is not null)
        {
            var (linked, failed) = await LinkBatchAsync([result], artifactUri, fmt, ct);
            EmitLinkSummary(linked, failed, linkBranch!, fmt);
        }

        // Update context if the published seed was the active item
        if (result.Status == SeedPublishStatus.Created && activeId == id.Value && result.NewId > 0)
            await contextStore.SetActiveWorkItemIdAsync(result.NewId, ct);

        return result.IsSuccess ? 0 : 1;
    }

    private void RenderSingleResult(SeedPublishResult result, string outputFormat)
    {
        var tree = new Twig.RenderTree.RenderTree([BuildResultRecord(result)]);
        rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    private void RenderBatchResult(SeedPublishBatchResult batch, string outputFormat)
    {
        var resultNodes = new List<RenderNode>(batch.Results.Count);
        foreach (var r in batch.Results)
            resultNodes.Add(BuildResultRecord(r));

        var cycleNodes = new List<RenderNode>(batch.CycleErrors.Count);
        foreach (var err in batch.CycleErrors)
        {
            cycleNodes.Add(new RenderNode.KeyValue("cycleError", RenderCell.String(err)));
        }

        var fields = new List<DocumentField>
        {
            new("results", new RenderNode.Section($"Seed Publish ({batch.Results.Count})", resultNodes)),
            new("cycleErrors", new RenderNode.Section(null, cycleNodes)),
            new("createdCount", new RenderNode.KeyValue("createdCount", RenderCell.Integer(batch.CreatedCount))),
            new("skippedCount", new RenderNode.KeyValue("skippedCount", RenderCell.Integer(batch.SkippedCount))),
            new("hasErrors", new RenderNode.KeyValue("hasErrors", RenderCell.Boolean(batch.HasErrors))),
        };

        if (batch.Results.Count == 0 && batch.CycleErrors.Count == 0)
        {
            fields.Add(new DocumentField(
                "empty",
                new RenderNode.KeyValue("status", RenderCell.String("No seeds to publish.")),
                Audience: RenderAudience.HumanOnly));
        }

        var tree = new Twig.RenderTree.RenderTree([new RenderNode.Document(null, fields)]);
        rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    private static RenderNode.Record BuildResultRecord(SeedPublishResult result)
    {
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["oldId"] = RenderCell.Integer(result.OldId),
            ["newId"] = RenderCell.Integer(result.NewId),
            ["title"] = RenderCell.String(result.Title ?? string.Empty),
            ["status"] = RenderCell.String(result.Status.ToString()),
            ["isSuccess"] = RenderCell.Boolean(result.IsSuccess),
            ["errorMessage"] = result.ErrorMessage is not null
                ? RenderCell.String(result.ErrorMessage)
                : new RenderCell(string.Empty, new RenderValue.Null()),
        };

        if (result.LinkWarnings.Count > 0)
            cells["linkWarnings"] = RenderCell.String(string.Join("; ", result.LinkWarnings));

        if (result.ValidationFailures.Count > 0)
        {
            var msgs = result.ValidationFailures.Select(f => $"[{f.Rule}] {f.Message}");
            cells["validationFailures"] = RenderCell.String(string.Join("; ", msgs));
        }

        return new RenderNode.Record("seedPublishResult", cells);
    }

    /// <summary>
    /// Resolves the branch artifact URI once upfront. Returns null if linking should be skipped
    /// (no linkBranch specified, dry-run, git service unavailable, or IDs unresolvable).
    /// When <paramref name="repoName"/> is provided, uses name-based resolution via
    /// <see cref="IAdoGitService.GetRepositoryIdByNameAsync"/>; otherwise falls back to
    /// workspace-configured <see cref="IAdoGitService.GetRepositoryIdAsync"/>.
    /// </summary>
    private async Task<string?> ResolveBranchArtifactUriAsync(
        string? linkBranch, string? repoName, bool dryRun, IOutputFormatter fmt, CancellationToken ct)
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

        var repoId = repoName is not null
            ? await adoGitService.GetRepositoryIdByNameAsync(repoName, ct)
            : await adoGitService.GetRepositoryIdAsync(ct);

        if (projectId is null || repoId is null)
        {
            var context = repoName is not null
                ? $"repository '{repoName}'"
                : "workspace-configured repository";
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Could not resolve {context}. Skipping branch linking."));
            return null;
        }

        var encodedBranch = Uri.EscapeDataString(linkBranch);
        return $"vstfs:///Git/Ref/{projectId}/{repoId}/GB{encodedBranch}";
    }

    /// <summary>
    /// Links a batch of publish results to the branch. Returns (linkedCount, failureCount).
    /// Best-effort: logs warnings on individual failures but continues linking remaining items.
    /// </summary>
    private async Task<(int Linked, int Failed)> LinkBatchAsync(
        IReadOnlyList<SeedPublishResult> results, string artifactUri, IOutputFormatter fmt, CancellationToken ct)
    {
        var linkedCount = 0;
        var linkFailures = 0;

        foreach (var result in results)
        {
            if (result.Status != SeedPublishStatus.Created || result.NewId <= 0)
                continue;

            try
            {
                var remote = await adoService.FetchAsync(result.NewId, ct);
                await adoGitService!.AddArtifactLinkAsync(
                    result.NewId, artifactUri, "ArtifactLink", remote.Revision, "Branch", ct);
                linkedCount++;
            }
            catch (Exception ex)
            {
                linkFailures++;
                Console.Error.WriteLine(fmt.FormatInfo(
                    $"Failed to link branch to #{result.NewId}: {ex.Message}"));
            }
        }

        return (linkedCount, linkFailures);
    }

    /// <summary>Emits a summary of branch linking results to stderr.</summary>
    private static void EmitLinkSummary(int linked, int failed, string branch, IOutputFormatter fmt)
    {
        if (linked == 0 && failed == 0)
            return;

        var summary = failed > 0
            ? $"Linked {linked}/{linked + failed} seeds to branch {branch} ({failed} failed)"
            : $"Linked {linked} seeds to branch {branch}";

        Console.Error.WriteLine(fmt.FormatInfo(summary));
    }
}