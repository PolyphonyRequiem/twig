using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed edit &lt;id&gt;</c>: opens the seed in an external editor,
/// parses changes, and saves the updated seed locally.
/// </summary>
public sealed class SeedEditCommand(
    IWorkItemRepository workItemRepo,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Edit seed fields in an external editor.</summary>
    public async Task<int> ExecuteAsync(
        int id,
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

        var fieldDefs = await fieldDefStore.GetAllAsync(ct);
        var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
        var edited = await editorLauncher.LaunchAsync(buffer, ct);

        if (edited is null)
        {
            Console.WriteLine(fmt.FormatInfo("Seed edit cancelled (editor aborted)."));
            return 0;
        }

        var parsedFields = SeedEditorFormat.Parse(edited, fieldDefs);

        var newTitle = parsedFields.TryGetValue("System.Title", out var parsedTitle) && !string.IsNullOrWhiteSpace(parsedTitle)
            ? parsedTitle : seed.Title;

        // Compute number of changed fields
        var changedCount = 0;
        if (!string.Equals(newTitle, seed.Title, StringComparison.Ordinal))
            changedCount++;

        foreach (var kvp in parsedFields)
        {
            if (string.Equals(kvp.Key, "System.Title", StringComparison.OrdinalIgnoreCase))
                continue;

            seed.Fields.TryGetValue(kvp.Key, out var oldValue);
            if (!string.Equals(oldValue, kvp.Value, StringComparison.Ordinal))
                changedCount++;
        }

        if (changedCount == 0)
        {
            Console.WriteLine(fmt.FormatInfo("No changes detected."));
            return 0;
        }

        var updated = seed.WithSeedFields(newTitle, parsedFields);
        await workItemRepo.SaveAsync(updated, ct);

        Console.WriteLine(fmt.FormatSuccess($"Updated seed #{id} {updated.Title} ({changedCount} field(s) changed)"));
        return 0;
    }
}
