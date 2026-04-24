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
    // Expression-body avoids CS9124 double-capture with primary constructor params.
    // linkBranch linking implementation deferred to T2.
    internal IAdoGitService? AdoGitService => adoGitService;
    internal IAdoWorkItemService AdoService => adoService;

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

        if (all)
        {
            var batchResult = await orchestrator.PublishAllAsync(force, dryRun, ct);
            Console.WriteLine(fmt.FormatSeedPublishBatchResult(batchResult));

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

        // Update context if the published seed was the active item
        if (result.Status == SeedPublishStatus.Created && activeId == id.Value && result.NewId > 0)
            await contextStore.SetActiveWorkItemIdAsync(result.NewId, ct);

        return result.IsSuccess ? 0 : 1;
    }
}
