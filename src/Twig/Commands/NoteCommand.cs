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
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null)
{
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
            // Launch editor
            noteText = await editorLauncher.LaunchAsync(
                $"# Note for #{item.Id} {item.Title}\n# Lines starting with # are comments and will be removed.\n\n");

            if (noteText is null)
            {
                Console.WriteLine(fmt.FormatInfo("Note cancelled (empty or unchanged)."));
                return 0;
            }

            // Strip comment lines
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

        // Store as pending change
        await pendingChangeStore.AddChangeAsync(
            item.Id,
            "note",
            fieldName: null,
            oldValue: null,
            newValue: noteText);

        // Mark dirty in cache
        item.AddNote(new PendingNote(noteText, DateTimeOffset.UtcNow, IsHtml: false));
        item.ApplyCommands();
        await workItemRepo.SaveAsync(item);

        Console.WriteLine(fmt.FormatSuccess($"Note added to #{item.Id} (pending)."));

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
}
