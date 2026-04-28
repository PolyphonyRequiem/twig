using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed reconcile</c>: repairs orphaned and stale seed_links
/// and parent references using the publish_id_map.
/// </summary>
public sealed class SeedReconcileCommand(
    SeedReconcileOrchestrator orchestrator,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Reconcile stale seed links and parent references.</summary>
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var result = await orchestrator.ReconcileAsync(ct);

        Console.WriteLine(fmt.FormatSeedReconcileResult(result));
        return 0;
    }
}
