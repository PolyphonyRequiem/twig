using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig note ["text"]</c>: adds a note to the active work item.
/// If text is provided inline, stores as pending. Otherwise launches editor.
/// </summary>
public sealed class NoteCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IAdoWorkItemService adoService,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
    private readonly IAdoWorkItemService _adoService = adoService;

    /// <summary>Add a note/comment to the active work item.</summary>
    public async Task<int> ExecuteAsync(string? text = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var resolved = await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        string? noteText = text;

        if (string.IsNullOrWhiteSpace(noteText))
        {
            noteText = await editorLauncher.LaunchAsync(
                $"# Note for #{item.Id} {item.Title}\n# Lines starting with # are comments and will be removed.\n\n");

            if (noteText is null)
            {
                Console.WriteLine(fmt.FormatInfo("Note cancelled (empty or unchanged)."));
                return 0;
            }

            var lines = noteText.Split('\n')
                .Where(l => !l.TrimStart().StartsWith('#'))
                .ToArray();
            noteText = string.Join('\n', lines).Trim();

            if (string.IsNullOrWhiteSpace(noteText))
            {
                Console.WriteLine(fmt.FormatInfo("Note cancelled (empty after stripping comments)."));
                return 0;
            }
        }

        string successMessage;

        if (item.IsSeed)
        {
            await StageLocallyAsync(item, noteText, ct);
            successMessage = fmt.FormatSuccess($"Note added to #{item.Id} (pending).");
        }
        else
        {
            try
            {
                await _adoService.AddCommentAsync(item.Id, noteText, ct);
                await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);

                try
                {
                    var updated = await _adoService.FetchAsync(item.Id, ct);
                    await workItemRepo.SaveAsync(updated, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Note pushed but cache may be stale — run 'twig sync' to resync ({ex.Message})");
                }

                successMessage = fmt.FormatSuccess($"Note added to #{item.Id}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Note staged locally (ADO unreachable): {ex.Message}");
                await StageLocallyAsync(item, noteText, ct);
                successMessage = fmt.FormatSuccess($"Note added to #{item.Id} (pending).");
            }
        }

        Console.WriteLine(successMessage);

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        var hints = hintEngine.GetHints("note", outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    private async Task StageLocallyAsync(Twig.Domain.Aggregates.WorkItem item, string noteText, CancellationToken ct)
    {
        await pendingChangeStore.AddChangeAsync(
            item.Id,
            "note",
            fieldName: null,
            oldValue: null,
            newValue: noteText);

        item.AddNote(new PendingNote(noteText, DateTimeOffset.UtcNow, IsHtml: false));
        item.ApplyCommands();
        await workItemRepo.SaveAsync(item, ct);
    }
}
