using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig nav back</c>, <c>twig nav fore</c>, and <c>twig nav history</c>:
/// chronological navigation through the context-switch history.
/// Back/fore set context directly (DD-04) to avoid recording new history entries.
/// Negative seed IDs are resolved at read time (DD-05) via <see cref="IPublishIdMapRepository"/>.
/// </summary>
public sealed class NavigationHistoryCommands(
    INavigationHistoryStore historyStore,
    IPublishIdMapRepository publishIdMapRepo,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    RenderingPipelineFactory? pipelineFactory = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Navigate backward in the navigation history.</summary>
    public async Task<int> BackAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var workItemId = await historyStore.GoBackAsync(ct);
        if (workItemId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Already at oldest entry in navigation history."));
            return 1;
        }

        // DD-05: Resolve seed IDs at read time
        var resolvedId = await ResolveSeedIdAsync(workItemId.Value, ct);

        // DD-04: Set context directly (bypass SetCommand to avoid recording history)
        await contextStore.SetActiveWorkItemIdAsync(resolvedId, ct);

        var item = await workItemRepo.GetByIdAsync(resolvedId, ct);
        if (item is not null)
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        else
            Console.WriteLine(fmt.FormatInfo($"#{resolvedId}"));

        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    /// <summary>Navigate forward in the navigation history.</summary>
    public async Task<int> ForeAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var workItemId = await historyStore.GoForwardAsync(ct);
        if (workItemId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Already at newest entry in navigation history."));
            return 1;
        }

        // DD-05: Resolve seed IDs at read time
        var resolvedId = await ResolveSeedIdAsync(workItemId.Value, ct);

        // DD-04: Set context directly (bypass SetCommand to avoid recording history)
        await contextStore.SetActiveWorkItemIdAsync(resolvedId, ct);

        var item = await workItemRepo.GetByIdAsync(resolvedId, ct);
        if (item is not null)
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        else
            Console.WriteLine(fmt.FormatInfo($"#{resolvedId}"));

        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    /// <summary>Display the navigation history with an optional interactive picker.</summary>
    public async Task<int> HistoryAsync(bool nonInteractive, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        // nonInteractive will be used in Epic 4 to suppress the interactive picker
        _ = nonInteractive;
        // pipelineFactory will be used here in Epic 4 for interactive picker
        _ = pipelineFactory;
        await Task.CompletedTask;
        var fmt = formatterFactory.GetFormatter(outputFormat);
        Console.Error.WriteLine(fmt.FormatError("nav history is not yet implemented."));
        return 1;
    }

    /// <summary>
    /// Resolves a seed ID to its published ADO ID if available.
    /// Negative IDs indicate local seeds; if published, the mapping is returned.
    /// Otherwise the original ID is returned unchanged.
    /// </summary>
    private async Task<int> ResolveSeedIdAsync(int workItemId, CancellationToken ct)
    {
        if (workItemId < 0)
        {
            var newId = await publishIdMapRepo.GetNewIdAsync(workItemId, ct);
            if (newId.HasValue)
                return newId.Value;
        }

        return workItemId;
    }
}
