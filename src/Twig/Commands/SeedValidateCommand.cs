using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed validate [id]</c>: validates seeds against publish rules.
/// With an ID, validates a single seed with detailed output.
/// Without an ID, validates all seeds with a summary.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// machine output is a Document with a results Table plus passed/total counts; human
/// output emits a streamed per-result line plus a summary line; minimal emits a
/// PASS/FAIL per result plus a summary.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting.
/// Note: the per-result <c>failures</c> array (previously nested) is now flattened
/// into a comma-joined <c>failures</c> column for the JSON shape.
/// </remarks>
public sealed class SeedValidateCommand(
    IWorkItemRepository workItemRepo,
    ISeedPublishRulesProvider rulesProvider,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Validate one or all seeds against publish rules.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var rules = await rulesProvider.GetRulesAsync(ct);

        if (id.HasValue)
        {
            var seed = await workItemRepo.GetByIdAsync(id.Value, ct);
            if (seed is null || !seed.IsSeed)
            {
                Console.Error.WriteLine(fmt.FormatError($"Seed #{id.Value} not found."));
                return 1;
            }

            var result = SeedValidator.Validate(seed, rules);
            Render(new[] { result }, outputFormat);
            return result.Passed ? 0 : 1;
        }

        var seeds = await workItemRepo.GetSeedsAsync(ct);
        if (seeds.Count == 0)
        {
            Render(Array.Empty<SeedValidationResult>(), outputFormat);
            return 0;
        }

        var results = new List<SeedValidationResult>(seeds.Count);
        foreach (var seed in seeds)
            results.Add(SeedValidator.Validate(seed, rules));

        Render(results, outputFormat);
        return results.Exists(r => !r.Passed) ? 1 : 0;
    }

    private void Render(IReadOnlyList<SeedValidationResult> results, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var passCount = 0;
        foreach (var r in results)
            if (r.Passed) passCount++;

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("seedId", "Seed"),
                new("title", "Title"),
                new("passed", "Passed"),
                new("failures", "Failures"),
            };
            var rows = new List<RenderRow>(results.Count);
            foreach (var r in results)
            {
                var failuresText = string.Join("; ", r.Failures.Select(f => $"{f.Rule}: {f.Message}"));
                rows.Add(new RenderRow("seedValidation", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["seedId"] = RenderCell.Integer(r.SeedId),
                    ["title"] = RenderCell.String(r.Title),
                    ["passed"] = RenderCell.Boolean(r.Passed),
                    ["failures"] = RenderCell.String(failuresText),
                }));
            }
            var fields = new List<DocumentField>(3)
            {
                new("results", new RenderNode.Table(null, columns, rows)),
                new("passed", new RenderNode.KeyValue("passed", RenderCell.Integer(passCount))),
                new("total", new RenderNode.KeyValue("total", RenderCell.Integer(results.Count))),
            };
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Document("seedValidation", fields),
            }));
            return;
        }

        if (lower == "minimal")
        {
            var nodes = new List<RenderNode>(results.Count + 1);
            foreach (var r in results)
                nodes.Add(new RenderNode.Text($"{(r.Passed ? "PASS" : "FAIL")} #{r.SeedId}"));
            nodes.Add(new RenderNode.Text($"{passCount}/{results.Count} passed"));
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
            return;
        }

        if (results.Count == 0)
        {
            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Text("No seeds to validate.", Severity.Info),
            }));
            return;
        }

        var humanNodes = new List<RenderNode>(results.Count * 2 + 1);
        foreach (var r in results)
        {
            var icon = r.Passed ? "✔" : "✘";
            var severity = r.Passed ? Severity.Success : Severity.Error;
            humanNodes.Add(new RenderNode.Text($"  {icon} #{r.SeedId} {r.Title}", severity));
            foreach (var f in r.Failures)
                humanNodes.Add(new RenderNode.Text($"      • {f.Rule}: {f.Message}", Severity.Error));
        }
        humanNodes.Add(new RenderNode.Text($"{passCount}/{results.Count} passed",
            passCount == results.Count ? Severity.Success : Severity.Warning));
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(humanNodes));
    }
}
