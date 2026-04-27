using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig seed new [--type &lt;type&gt;] [--editor] "title"</c>: creates a seed work item
/// locally under the active parent without any ADO interaction.
/// Also backs the bare <c>twig seed "title"</c> shortcut for backward compatibility.
/// </summary>
public sealed class SeedNewCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IProcessConfigurationProvider processConfigProvider,
    IFieldDefinitionStore fieldDefStore,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    TwigConfiguration config,
    SeedFactory seedFactory)
{
    /// <summary>Create a new local seed work item (no ADO push).</summary>
    public async Task<int> ExecuteAsync(
        string? title,
        string? type = null,
        bool editor = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Title is required unless --editor is used (editor can supply it)
        if (!editor && string.IsNullOrWhiteSpace(title))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig seed new --title \"title\" [--type <type>]"));
            return 2;
        }

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (!resolved.TryGetWorkItem(out var parent, out var errorId, out var errorReason) && errorId is not null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{errorId} is unreachable: {errorReason}"));
            return 1;
        }

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

        // Initialize seed counter from DB to avoid ID collisions (D7)
        var minSeedId = await workItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedFactory.InitializeSeedCounter(minSeedId.Value);

        // Use placeholder title for editor-only flow when no title provided
        var seedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var seedResult = seedFactory.Create(seedTitle, parent, processConfig, typeOverride,
            config.User.DisplayName);
        if (!seedResult.IsSuccess)
        {
            Console.Error.WriteLine(fmt.FormatError(seedResult.Error));
            return 1;
        }

        var seed = seedResult.Value;

        if (editor)
        {
            // Editor workflow: generate buffer, launch editor, parse result, apply fields
            var fieldDefs = await fieldDefStore.GetAllAsync(ct);
            var buffer = SeedEditorFormat.Generate(seed, fieldDefs);
            var edited = await editorLauncher.LaunchAsync(buffer, ct);

            if (edited is null)
            {
                Console.WriteLine(fmt.FormatInfo("Seed creation cancelled (editor aborted)."));
                return 0;
            }

            var parsedFields = SeedEditorFormat.Parse(edited, fieldDefs);

            var newTitle = parsedFields.TryGetValue("System.Title", out var parsedTitle) && !string.IsNullOrWhiteSpace(parsedTitle)
                ? parsedTitle : seedTitle;
            seed = seed.WithSeedFields(newTitle, parsedFields);
        }

        // Persist locally — no ADO interaction
        await workItemRepo.SaveAsync(seed, ct);

        Console.WriteLine(fmt.FormatSuccess($"Created local seed: #{seed.Id} {seed.Title} ({seed.Type})"));

        var hints = hintEngine.GetHints("seed",
            outputFormat: outputFormat,
            createdId: seed.Id);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
