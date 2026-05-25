using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Services.Mutation;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig delete &lt;id&gt;</c>: permanently deletes a single ADO work item
/// with multiple safety guards (seed check, link check, interactive confirmation).
/// Requires an explicit ID — does NOT default to the active work item.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: success/info output is built as a <see cref="RenderTree.RenderTree"/>
/// per output format. <see cref="OutputFormatterFactory"/> is retained only for
/// stderr error formatting (matching the SetCommand/NoteCommand/StateCommand/UpdateCommand/PatchCommand migrations).
/// </remarks>
public sealed class DeleteCommand(
    ActiveItemResolver activeItemResolver,
    DeleteWorkflow deleteWorkflow,
    IConsoleInput consoleInput,
    CommandContext ctx,
    RendererFactory? rendererFactory = null)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Permanently delete a work item from Azure DevOps.</summary>
    /// <param name="id">The work item ID to delete (required).</param>
    /// <param name="force">Skip the interactive confirmation prompt.</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    public async Task<int> ExecuteAsync(
        int id,
        bool force = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("delete", outputFormat);
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        int exitCode;

        try
        {
            exitCode = await ExecuteCoreAsync(id, force, outputFormat, fmt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatError($"Delete failed: {ex.Message}"));
            exitCode = 1;
        }

        scope.Complete(exitCode);
        TelemetryHelper.TrackCommand(
            ctx.TelemetryClient,
            "delete",
            outputFormat,
            exitCode,
            scope.StartTimestamp,
            extraProperties: new Dictionary<string, string>
            {
                ["used_force"] = force.ToString(),
            });

        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(
        int id,
        bool force,
        string outputFormat,
        IOutputFormatter fmt,
        CancellationToken ct)
    {
        // 1. Resolve item from cache or ADO (for seed guard + early not-found error)
        var resolved = await activeItemResolver.ResolveByIdAsync(id, ct);
        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found. Consider 'twig state Closed' for items you no longer need."
                : $"Work item #{id} could not be resolved: {errorReason}"));
            return 1;
        }

        // 2. Seed guard
        if (item.IsSeed)
        {
            _stderr.WriteLine(fmt.FormatError($"#{id} is a seed. Use 'twig seed discard {id}' instead."));
            return 1;
        }

        // 3. Workflow: fresh fetch + link guard
        var preparation = await deleteWorkflow.PrepareAsync(id, ct);
        WorkItem freshItem;
        switch (preparation)
        {
            case DeletePreparation.FetchFailed f:
                _stderr.WriteLine(fmt.FormatError($"Failed to fetch #{id} for deletion: {f.Reason}"));
                return 1;
            case DeletePreparation.BlockedByLinks b:
                _stderr.WriteLine(fmt.FormatError(
                    $"Cannot delete #{id} '{b.FreshItem.Title}' — it has {b.TotalLinkCount} link(s): {b.LinkSummary}. " +
                    "Remove all links before deleting. Consider 'twig state Closed' instead — it preserves history and is reversible."));
                return 1;
            case DeletePreparation.Ready r:
                freshItem = r.FreshItem;
                break;
            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled DeletePreparation: {preparation.GetType().Name}");
        }

        // 4. Confirmation prompt
        if (!force)
        {
            if (consoleInput.IsOutputRedirected)
            {
                _stderr.WriteLine(fmt.FormatError(
                    "Cannot confirm deletion in non-interactive mode. Use --force to bypass confirmation."));
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"  ID:    #{freshItem.Id}");
            Console.WriteLine($"  Title: {freshItem.Title}");
            Console.WriteLine($"  Type:  {freshItem.Type}");
            Console.WriteLine($"  State: {freshItem.State}");
            Console.WriteLine();
            Console.WriteLine("⚠ This action is PERMANENT. Consider 'twig state Closed' instead — it preserves history and is reversible.");
            Console.WriteLine();
            Console.Write("Type 'yes' to confirm deletion: ");

            var response = consoleInput.ReadLine();
            if (!string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                RenderCancelled(outputFormat);
                return 0;
            }
        }

        // 5. Workflow: audit + delete + cache cleanup + prompt-state
        var outcome = await deleteWorkflow.ExecuteAsync(freshItem, ct);
        switch (outcome)
        {
            case DeleteOutcome.AdoFailed f:
                _stderr.WriteLine(fmt.FormatError($"Delete failed: {f.Reason}"));
                return 1;
            case DeleteOutcome.Deleted d:
                var hints = ctx.HintEngine.GetHints("delete", item: freshItem, outputFormat: outputFormat);
                RenderDeleted(freshItem, hints, outputFormat);
                return 0;
            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled DeleteOutcome: {outcome.GetType().Name}");
        }
    }

    private void RenderDeleted(WorkItem item, IReadOnlyList<string> hints, string outputFormat)
    {
        var message = $"Deleted #{item.Id} '{item.Title}'.";
        var tree = BuildDeletedTree(item, message, hints, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private void RenderCancelled(string outputFormat)
    {
        const string message = "Delete cancelled.";
        var tree = BuildCancelledTree(message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
    }

    private static RenderTree.RenderTree BuildDeletedTree(
        WorkItem item,
        string message,
        IReadOnlyList<string> hints,
        string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isMachine = lower is "json" or "json-full" or "json-compact" or "minimal" or "ids";
        var nodes = new List<RenderNode>(capacity: 1 + (isMachine ? 0 : hints.Count));

        nodes.Add(lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildDeletedRecord(item, message),
            _ => new RenderNode.Text(message, Severity.Success),
        });

        // Hints only on human output. Adding Hint nodes for JSON formats would
        // turn a single-root Record document into an array with a `kind` discriminator.
        if (!isMachine)
        {
            foreach (var hint in hints)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                    nodes.Add(new RenderNode.Hint(hint));
            }
        }

        return new RenderTree.RenderTree(nodes);
    }

    private static RenderTree.RenderTree BuildCancelledTree(string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildCancelledRecord(message),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildDeletedRecord(WorkItem item, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(item.Id.ToString(), new RenderValue.Integer(item.Id)),
            ["title"] = new RenderCell(item.Title, new RenderValue.String(item.Title)),
            ["type"] = new RenderCell(item.Type.Value, new RenderValue.String(item.Type.Value)),
            ["state"] = new RenderCell(item.State, new RenderValue.String(item.State)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("deleted", fields);
    }

    private static RenderNode BuildCancelledRecord(string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["cancelled"] = new RenderCell("true", new RenderValue.Boolean(true)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("deleteCancelled", fields);
    }
}
