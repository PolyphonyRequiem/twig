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
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig note ["text"]</c>: adds a note (ADO comment) to the active work item.
/// If text is provided inline it is pushed immediately; otherwise an editor is launched.
/// </summary>
/// <remarks>
/// <para>
/// Adapter around <see cref="NoteWorkflow"/>. This command owns:
/// argument parsing, editor launch, output formatting, and hint rendering.
/// The workflow owns: push-with-fallback, cache resync, prompt-state write,
/// and pending-changes bookkeeping.
/// </para>
/// <para>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: success/info/hint output is built as a <see cref="RenderTree.RenderTree"/>
/// per output format. <see cref="OutputFormatterFactory"/> is retained only for
/// stderr error formatting (matching the SetCommand migration pattern).
/// </para>
/// </remarks>
public sealed class NoteCommand(
    ActiveItemResolver activeItemResolver,
    IEditorLauncher editorLauncher,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    NoteWorkflow noteWorkflow,
    RendererFactory? rendererFactory = null,
    TextReader? stdinReader = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    private readonly TextReader _stdin = stdinReader ?? Console.In;

    /// <summary>Add a note/comment to the active work item.</summary>
    public async Task<int> ExecuteAsync(string? text = null, int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, string? format = null, string? filePath = null, bool readStdin = false, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
        {
            Console.Error.WriteLine(fmt.FormatError(formatError));
            return 2;
        }

        // AB#617: --file/--stdin, with the same semantics `twig update` already has.
        // Resolved BEFORE the active-item lookup so a caller who names two sources —
        // or a file that does not exist — is told so even when no item is active.
        var body = await TextBodySource.ResolveAsync(
            text, filePath, readStdin, _stdin,
            inlineLabel: "note text",
            fileFlag: "--file",
            stdinFlag: "--stdin",
            trimTrailingNewline: format is null,
            ct);
        if (body.Outcome is TextBodySource.Outcome.Ambiguous or TextBodySource.Outcome.FileNotFound)
        {
            Console.Error.WriteLine(fmt.FormatError(body.Error!));
            return 2;
        }

        // 🔴 An EMPTY --file or --stdin must not silently fall through to the editor.
        // That would open an interactive editor in a pipeline — a hang, not a note —
        // and report success for a body the caller never wrote. Checked HERE, with the
        // other input validation, rather than after the active-item lookup: an empty
        // --stdin is a bad invocation regardless of whether an item happens to be
        // active, and reporting "No active work item" for it names the wrong fault.
        if (body.Origin is TextBodySource.Origin.File or TextBodySource.Origin.Stdin
            && string.IsNullOrWhiteSpace(body.Value))
        {
            Console.Error.WriteLine(fmt.FormatError(body.Origin is TextBodySource.Origin.File
                ? $"File is empty: {filePath}"
                : "Standard input is empty."));
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

        // Outcome.None keeps the editor path: `twig note` with no source at all still
        // opens an editor, which is the documented spelling.
        string? noteText = body.Value;

        if (string.IsNullOrWhiteSpace(noteText))
        {
            noteText = await editorLauncher.LaunchAsync(
                $"# Note for #{item.Id} {item.Title}\n# Lines starting with # are comments and will be removed.\n\n");

            if (noteText is null)
            {
                RenderInfo("Note cancelled (empty or unchanged).", outputFormat);
                return 0;
            }

            var lines = noteText.Split('\n')
                .Where(l => !l.TrimStart().StartsWith('#'))
                .ToArray();
            noteText = string.Join('\n', lines).Trim();

            if (string.IsNullOrWhiteSpace(noteText))
            {
                RenderInfo("Note cancelled (empty after stripping comments).", outputFormat);
                return 0;
            }
        }

        var commentResolution = HtmlFieldFormatter.ResolveComment(noteText, format);
        var outcome = await noteWorkflow.ExecuteAsync(item, commentResolution.EffectiveValue, commentResolution.IsHtml, ct);

        bool isPending;
        string successMessage;
        switch (outcome)
        {
            case NoteOutcome.Pushed pushed:
                foreach (var warning in pushed.Warnings)
                    Console.Error.WriteLine(warning);
                isPending = false;
                successMessage = $"Note added to #{item.Id}.";
                break;

            case NoteOutcome.Staged staged:
                if (staged.WasOfflineFallback)
                    Console.Error.WriteLine($"Note staged locally (ADO unreachable): {staged.FailureReason}");
                foreach (var warning in staged.Warnings)
                    Console.Error.WriteLine(warning);
                isPending = true;
                successMessage = $"Note added to #{item.Id} (pending).";
                break;

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled NoteOutcome: {outcome.GetType().Name}");
        }

        var hints = hintEngine.GetHints("note", outputFormat: outputFormat, noteWasStaged: isPending);
        var tree = BuildSuccessTree(item.Id, isPending, successMessage, hints, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);

        return 0;
    }

    private void RenderInfo(string message, string outputFormat)
    {
        var tree = BuildInfoTree(message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildSuccessTree(int itemId, bool isPending, string message, IReadOnlyList<string> hints, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var nodes = new List<RenderNode>(capacity: 1 + hints.Count);

        nodes.Add(lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" => BuildNoteAddedRecord(itemId, isPending, message),
            _ => new RenderNode.Text(message, Severity.Success),
        });

        foreach (var hint in hints)
        {
            if (!string.IsNullOrWhiteSpace(hint))
                nodes.Add(new RenderNode.Hint(hint));
        }

        return new RenderTree.RenderTree(nodes);
    }

    private static RenderTree.RenderTree BuildInfoTree(string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" => BuildNoteCancelledRecord(message),
            "ids" => new RenderNode.Text(string.Empty),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildNoteAddedRecord(int itemId, bool isPending, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(itemId.ToString(), new RenderValue.Integer(itemId)),
            ["isPending"] = new RenderCell(isPending ? "true" : "false", new RenderValue.Boolean(isPending)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("noteAdded", fields);
    }

    private static RenderNode BuildNoteCancelledRecord(string reason)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["reason"] = new RenderCell(reason, new RenderValue.String(reason)),
        };
        return new RenderNode.Record("noteCancelled", fields);
    }
}