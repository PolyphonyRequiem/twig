using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed reconcile</c>: repairs orphaned and stale seed_links
/// and parent references using the publish_id_map.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// machine output is a Document of counts plus a warnings Table; human output is a
/// labelled summary, minimal is a single key line.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// </remarks>
public sealed class SeedReconcileCommand(
    SeedReconcileOrchestrator orchestrator,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    private readonly OutputFormatterFactory _formatterFactory = formatterFactory;

    /// <summary>Reconcile stale seed links and parent references.</summary>
    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        _ = _formatterFactory;
        var result = await orchestrator.ReconcileAsync(ct);

        var tree = BuildTree(result, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        return 0;
    }

    private static RenderTree.RenderTree BuildTree(SeedReconcileResult result, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower switch
        {
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderTree.RenderTree(new[] { BuildMachineDocument(result) }),
            "minimal" =>
                new RenderTree.RenderTree(new[] { BuildMinimal(result) }),
            _ => new RenderTree.RenderTree(BuildHuman(result)),
        };
    }

    private static RenderNode BuildMachineDocument(SeedReconcileResult result)
    {
        var warningsColumns = new List<RenderColumn>
        {
            new("message", "Message"),
        };
        var warningRows = new List<RenderRow>(result.Warnings.Count);
        foreach (var w in result.Warnings)
        {
            warningRows.Add(new RenderRow("warning", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["message"] = RenderCell.String(w),
            }));
        }

        var fields = new List<DocumentField>(5)
        {
            new("linksRepaired", new RenderNode.KeyValue("linksRepaired", RenderCell.Integer(result.LinksRepaired))),
            new("linksRemoved", new RenderNode.KeyValue("linksRemoved", RenderCell.Integer(result.LinksRemoved))),
            new("parentIdsFixed", new RenderNode.KeyValue("parentIdsFixed", RenderCell.Integer(result.ParentIdsFixed))),
            new("nothingToDo", new RenderNode.KeyValue("nothingToDo", RenderCell.Boolean(result.NothingToDo))),
            new("warnings", new RenderNode.Table(null, warningsColumns, warningRows)),
        };
        return new RenderNode.Document("seedReconcile", fields);
    }

    private static RenderNode BuildMinimal(SeedReconcileResult result)
    {
        if (result.NothingToDo)
            return new RenderNode.Text("RECONCILE NOTHING");
        var parts = new List<string>();
        if (result.LinksRepaired > 0) parts.Add($"REPAIRED {result.LinksRepaired}");
        if (result.LinksRemoved > 0) parts.Add($"REMOVED {result.LinksRemoved}");
        if (result.ParentIdsFixed > 0) parts.Add($"PARENT {result.ParentIdsFixed}");
        return new RenderNode.Text($"RECONCILE {string.Join(' ', parts)}");
    }

    private static IReadOnlyList<RenderNode> BuildHuman(SeedReconcileResult result)
    {
        if (result.NothingToDo)
            return new[] { (RenderNode)new RenderNode.Text("Nothing to reconcile.", Severity.Success) };

        var nodes = new List<RenderNode>
        {
            new RenderNode.Text("Seed Reconciliation"),
            new RenderNode.Text(new string('─', 40)),
        };
        if (result.LinksRepaired > 0)
            nodes.Add(new RenderNode.Text($"  ✔ Links repaired:   {result.LinksRepaired}", Severity.Success));
        if (result.LinksRemoved > 0)
            nodes.Add(new RenderNode.Text($"  ✔ Links removed:    {result.LinksRemoved}", Severity.Warning));
        if (result.ParentIdsFixed > 0)
            nodes.Add(new RenderNode.Text($"  ✔ Parent IDs fixed:  {result.ParentIdsFixed}", Severity.Success));
        foreach (var warning in result.Warnings)
            nodes.Add(new RenderNode.Text($"  ⚠ {warning}", Severity.Warning));
        return nodes;
    }
}
