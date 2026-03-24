using Twig.Domain.Interfaces;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed discard &lt;id&gt;</c>: prompts for confirmation and deletes
/// the local seed. Use <c>--yes</c> to skip the confirmation prompt.
/// </summary>
public sealed class SeedDiscardCommand(
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
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

        if (!yes)
        {
            Console.Write($"Discard seed #{id} '{seed.Title}'? (y/N) ");
            var response = consoleInput.ReadLine();
            if (!string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(fmt.FormatInfo("Discard cancelled."));
                return 0;
            }
        }

        await seedLinkRepo.DeleteLinksForItemAsync(id, ct);
        await workItemRepo.DeleteByIdAsync(id, ct);
        Console.WriteLine(fmt.FormatSuccess($"Discarded seed #{id}"));
        return 0;
    }
}
