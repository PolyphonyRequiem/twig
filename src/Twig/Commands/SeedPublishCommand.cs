using Twig.Domain.Services;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed publish [id] [--all] [--force] [--dry-run]</c>:
/// publishes seeds to Azure DevOps as real work items.
/// With an ID, publishes a single seed.
/// With --all, publishes all seeds in topological order.
/// </summary>
public sealed class SeedPublishCommand(
    SeedPublishOrchestrator orchestrator,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Publish one or all seeds to Azure DevOps.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool all = false,
        bool force = false,
        bool dryRun = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (all)
        {
            var batchResult = await orchestrator.PublishAllAsync(force, dryRun, ct);
            Console.WriteLine(fmt.FormatSeedPublishBatchResult(batchResult));
            return batchResult.HasErrors ? 1 : 0;
        }

        if (!id.HasValue)
        {
            Console.Error.WriteLine(fmt.FormatError("Specify a seed ID or use --all."));
            return 1;
        }

        var result = await orchestrator.PublishAsync(id.Value, force, dryRun, ct);
        Console.WriteLine(fmt.FormatSeedPublishResult(result));
        return result.IsSuccess ? 0 : 1;
    }
}
