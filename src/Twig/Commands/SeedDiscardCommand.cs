using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed discard &lt;id&gt;</c>: prompts for confirmation and deletes
/// the local seed and its descendants. Use <c>--yes</c> to skip the confirmation prompt.
/// </summary>
public sealed class SeedDiscardCommand(
    IWorkItemRepository workItemRepo,
    SeedDiscardOrchestrator orchestrator,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory)
{
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
                Console.WriteLine(fmt.FormatInfo("Discard cancelled."));
                return 0;
            }
        }

        await orchestrator.ExecuteDiscardAsync(plan, ct);

        var successMsg = plan.HasDescendants
            ? $"Discarded seed #{id} {plan.TargetTitle} and {plan.DescendantCount} descendant{(plan.DescendantCount == 1 ? "" : "s")}"
            : $"Discarded seed #{id} {plan.TargetTitle}";
        Console.WriteLine(fmt.FormatSuccess(successMsg));
        return 0;
    }
}
