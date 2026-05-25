using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed discard &lt;id&gt;</c>: prompts for confirmation and deletes
/// the local seed and its descendants. Use <c>--yes</c> to skip the confirmation prompt.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// success/info output is built as a <see cref="RenderTree.RenderTree"/> per output format.
/// <see cref="OutputFormatterFactory"/> is retained only for stderr error formatting
/// (matching the SetCommand/NoteCommand/StateCommand/UpdateCommand/PatchCommand/DeleteCommand migrations).
/// </remarks>
public sealed class SeedDiscardCommand(
    IWorkItemRepository workItemRepo,
    SeedDiscardOrchestrator orchestrator,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Discard (delete) a local seed.</summary>
    public async Task<int> ExecuteAsync(
        int id,
        bool yes = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var seed = await workItemRepo.GetByIdAsync(id, ct);
        if (seed is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Seed #{id} not found."));
            return 1;
        }

        if (!seed.IsSeed)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{id} is not a seed."));
            return 1;
        }

        var plan = await orchestrator.BuildDiscardPlanAsync(id, ct);
        if (plan is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Seed #{id} not found."));
            return 1;
        }

        if (!yes)
        {
            var prompt = plan.HasDescendants
                ? $"Discard seed #{id} '{plan.TargetTitle}' and {plan.DescendantCount} descendant{(plan.DescendantCount == 1 ? "" : "s")}? (y/N) "
                : $"Discard seed #{id} '{plan.TargetTitle}'? (y/N) ";
            Console.Write(prompt);
            var response = consoleInput.ReadLine();
            if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                RenderCancelled(outputFormat);
                return 0;
            }
        }

        await orchestrator.ExecuteDiscardAsync(plan, ct);

        var successMsg = plan.HasDescendants
            ? $"Discarded seed #{id} {plan.TargetTitle} and {plan.DescendantCount} descendant{(plan.DescendantCount == 1 ? "" : "s")}"
            : $"Discarded seed #{id} {plan.TargetTitle}";
        RenderDiscarded(id, plan.TargetTitle, plan.DescendantCount, successMsg, outputFormat);
        return 0;
    }

    private void RenderDiscarded(int id, string title, int descendantCount, string message, string outputFormat)
    {
        var tree = BuildDiscardedTree(id, title, descendantCount, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderCancelled(string outputFormat)
    {
        const string message = "Discard cancelled.";
        var tree = BuildCancelledTree(message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildDiscardedTree(
        int id, string title, int descendantCount, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildDiscardedRecord(id, title, descendantCount, message),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderTree.RenderTree BuildCancelledTree(string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildCancelledRecord(message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildDiscardedRecord(int id, string title, int descendantCount, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(id.ToString(), new RenderValue.Integer(id)),
            ["title"] = new RenderCell(title, new RenderValue.String(title)),
            ["descendantCount"] = new RenderCell(descendantCount.ToString(), new RenderValue.Integer(descendantCount)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("seedDiscarded", fields);
    }

    private static RenderNode BuildCancelledRecord(string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["cancelled"] = new RenderCell("true", new RenderValue.Boolean(true)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("seedDiscardCancelled", fields);
    }
}
