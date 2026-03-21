using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed [--type &lt;type&gt;] "title"</c>: creates a seed work item
/// under the active parent, pushes to ADO, and caches locally.
/// </summary>
public sealed class SeedCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine)
{
    /// <summary>Create a new child work item (seed) under the active item.</summary>
    public async Task<int> ExecuteAsync(string title, string? type = null, string outputFormat = "human", CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig seed [--type <type>] \"title\""));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var parent, out var errorId, out var errorReason) && errorId is not null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{errorId} is unreachable: {errorReason}"));
            return 1;
        }

        // Resolve process configuration
        var processConfig = processConfigProvider.GetConfiguration();

        WorkItemType? typeOverride = null;
        if (type is not null)
        {
            var typeResult = WorkItemType.Parse(type);
            if (!typeResult.IsSuccess)
            {
                Console.Error.WriteLine(fmt.FormatError(typeResult.Error));
                return 1;
            }
            typeOverride = typeResult.Value;
        }

        var seedResult = SeedFactory.Create(title, parent, processConfig, typeOverride);
        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        // Push to ADO
        Console.WriteLine(fmt.FormatInfo($"Creating {seed.Type} in ADO..."));
        var newId = await adoService.CreateAsync(seed);

        // Fetch the created item to get full data
        var created = await adoService.FetchAsync(newId);
        await workItemRepo.SaveAsync(created);

        Console.WriteLine(fmt.FormatSuccess($"Created: #{newId} {title} ({seed.Type})"));

        var hints = hintEngine.GetHints("seed",
            outputFormat: outputFormat,
            createdId: newId);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
