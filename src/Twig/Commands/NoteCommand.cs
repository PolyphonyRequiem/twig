using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Services.Mutation;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig note ["text"]</c>: adds a note (ADO comment) to the active work item.
/// If text is provided inline it is pushed immediately; otherwise an editor is launched.
/// </summary>
/// <remarks>
/// Adapter around <see cref="NoteWorkflow"/>. This command owns:
/// argument parsing, editor launch, output formatting, and hint rendering.
/// The workflow owns: push-with-fallback, cache resync, prompt-state write,
/// and pending-changes bookkeeping.
/// </remarks>
public sealed class NoteCommand(
    ActiveItemResolver activeItemResolver,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    NoteWorkflow noteWorkflow)
{
    /// <summary>Add a note/comment to the active work item.</summary>
    public async Task<int> ExecuteAsync(string? text = null, int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, string? format = null, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
        {
            Console.Error.WriteLine(fmt.FormatError(formatError));
            return 2;
        }

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync();
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            Console.Error.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
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

        var commentResolution = HtmlFieldFormatter.ResolveComment(noteText, format);
        var outcome = await noteWorkflow.ExecuteAsync(item, commentResolution.EffectiveValue, commentResolution.IsHtml, ct);

        string successMessage;
        switch (outcome)
        {
            case NoteOutcome.Pushed pushed:
                foreach (var warning in pushed.Warnings)
                    Console.Error.WriteLine(warning);
                successMessage = fmt.FormatSuccess($"Note added to #{item.Id}.");
                break;

            case NoteOutcome.Staged staged:
                if (staged.WasOfflineFallback)
                    Console.Error.WriteLine($"Note staged locally (ADO unreachable): {staged.FailureReason}");
                foreach (var warning in staged.Warnings)
                    Console.Error.WriteLine(warning);
                successMessage = fmt.FormatSuccess($"Note added to #{item.Id} (pending).");
                break;

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled NoteOutcome: {outcome.GetType().Name}");
        }

        Console.WriteLine(successMessage);

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