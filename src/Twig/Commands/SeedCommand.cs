using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;
/// Implements <c>twig seed [--type &lt;type&gt;] "title"</c>: creates a seed work item
/// under the active parent, pushes to ADO, and caches locally.
/// </summary>
public sealed class SeedCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IProcessConfigurationProvider processConfigProvider,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine)
{
    /// <summary>Create a new child work item (seed) under the active item.</summary>
    public async Task<int> ExecuteAsync(string title, string? type = null, string outputFormat = "human")
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig seed [--type <type>] \"title\""));
            return 2;
        }

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        WorkItem? parent = null;

        if (activeId.HasValue)
        {
            parent = await workItemRepo.GetByIdAsync(activeId.Value);
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
