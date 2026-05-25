using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Formatters;
using Twig.Infrastructure.Services.Mutation;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig discard &lt;id&gt;</c> and <c>twig discard --all</c>:
/// drops pending changes (notes and field edits) for a single work item or all dirty items.
/// Seeds are excluded — use <c>twig seed discard</c> for seeds.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/> seam:
/// non-mutating info messages (cancelled, no changes, phantom cleared) emit records.
/// The single/all-items success JSON shape continues to be emitted by the existing
/// <see cref="WriteJson"/> path (wire format committed) — only the human success line
/// is routed through the renderer. <see cref="OutputFormatterFactory"/> retained for
/// stderr error formatting.
/// </remarks>
public sealed class DiscardCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    DiscardWorkflow discardWorkflow,
    IPromptStateWriter? promptStateWriter = null,
    ITelemetryClient? telemetryClient = null,
    RendererFactory? rendererFactory = null)
{
    private static readonly JsonWriterOptions JsonWriterOptions = new() { Indented = true };
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Discard pending changes for a single item or all dirty items.</summary>
    public async Task<int> ExecuteAsync(
        int? id = null,
        bool all = false,
        bool yes = false,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        using var scope = new CommandActivityScope("discard", outputFormat);
        var fmt = formatterFactory.GetFormatter(outputFormat);

        int exitCode;
        int itemCount = 0;
        try
        {
            if (id.HasValue && all)
            {
                Console.Error.WriteLine(fmt.FormatError("Specify either <id> or --all, not both."));
                exitCode = 1;
            }
            else if (!id.HasValue && !all)
            {
                Console.Error.WriteLine(fmt.FormatError("Specify <id> or --all. Run 'twig discard --help' for usage."));
                exitCode = 1;
            }
            else
            {
                (exitCode, itemCount) = all
                    ? await ExecuteAllAsync(fmt, yes, outputFormat, ct)
                    : await ExecuteSingleAsync(id!.Value, fmt, yes, outputFormat, ct);
            }

            scope.Complete(exitCode);
            telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "discard",
                ["exit_code"] = exitCode.ToString(),
                ["output_format"] = outputFormat,
                ["item_count"] = itemCount.ToString(),
                ["used_all"] = all.ToString(),
            }, new Dictionary<string, double>
            {
                ["duration_ms"] = Stopwatch.GetElapsedTime(scope.StartTimestamp).TotalMilliseconds,
            });
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
    }

    private async Task<(int ExitCode, int ItemCount)> ExecuteSingleAsync(
        int itemId,
        IOutputFormatter fmt,
        bool yes,
        string outputFormat,
        CancellationToken ct)
    {
        var item = await workItemRepo.GetByIdAsync(itemId, ct);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{itemId} not found."));
            return (1, 0);
        }

        if (item.IsSeed)
        {
            Console.Error.WriteLine(fmt.FormatError($"#{itemId} is a seed. Use 'twig seed discard {itemId}' instead."));
            return (1, 0);
        }

        var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(itemId, ct);

        if (item.IsDirty || notes > 0 || fieldEdits > 0)
        {
            if (notes > 0 || fieldEdits > 0)
            {
                var summary = FormatChangeSummary(notes, fieldEdits);
                if (!Confirm($"Discard {summary} for #{itemId} '{item.Title}'", yes, outputFormat))
                    return (0, 0);
            }
        }

        var outcome = await discardWorkflow.ExecuteAsync(item, ct);

        switch (outcome)
        {
            case DiscardOutcome.NoChanges:
                RenderInfoRecord("discardNoChanges", $"#{itemId} '{item.Title}' has no pending changes.", itemId, outputFormat);
                return (0, 0);

            case DiscardOutcome.PhantomDirtyCleared phantom:
                RenderInfoRecord("discardPhantomCleared", $"#{itemId} '{item.Title}' had a stale dirty flag (cleared).", itemId, outputFormat);
                foreach (var w in phantom.Warnings) Console.Error.WriteLine(w);
                return (0, 0);

            case DiscardOutcome.Discarded discarded:
                var summaryText = FormatChangeSummary(discarded.NotesCount, discarded.FieldEditsCount);
                if (outputFormat.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    WriteJson([(itemId, discarded.NotesCount, discarded.FieldEditsCount)], discarded.NotesCount, discarded.FieldEditsCount);
                else
                    RenderHumanSuccess($"Discarded {summaryText} for #{itemId} '{item.Title}'.", outputFormat);
                foreach (var w in discarded.Warnings) Console.Error.WriteLine(w);
                return (0, 1);

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled DiscardOutcome: {outcome.GetType().Name}");
        }
    }

    private async Task<(int ExitCode, int ItemCount)> ExecuteAllAsync(
        IOutputFormatter fmt,
        bool yes,
        string outputFormat,
        CancellationToken ct)
    {
        var dirtyItems = await workItemRepo.GetDirtyItemsAsync(ct);

        var nonSeedDirty = new List<(int Id, int Notes, int FieldEdits)>();
        foreach (var item in dirtyItems)
        {
            if (item.IsSeed) continue;
            var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(item.Id, ct);
            nonSeedDirty.Add((item.Id, notes, fieldEdits));
        }

        if (nonSeedDirty.Count == 0)
        {
            await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);
            RenderInfoRecord("discardAllEmpty", "No pending changes to discard.", null, outputFormat);
            return (0, 0);
        }

        var totalNotes = nonSeedDirty.Sum(x => x.Notes);
        var totalFieldEdits = nonSeedDirty.Sum(x => x.FieldEdits);
        var totalItems = nonSeedDirty.Count;
        var aggregateSummary = $"{totalItems} item{(totalItems != 1 ? "s" : "")} ({FormatChangeSummary(totalNotes, totalFieldEdits)})";

        if (!Confirm($"Discard all pending changes for {aggregateSummary}", yes, outputFormat))
            return (0, 0);

        await pendingChangeStore.ClearAllChangesAsync(ct);
        await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);

        if (outputFormat.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            WriteJson(nonSeedDirty, totalNotes, totalFieldEdits);
        else
            RenderHumanSuccess($"Discarded all pending changes for {aggregateSummary}.", outputFormat);

        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return (0, nonSeedDirty.Count);
    }

    private static void WriteJson(List<(int Id, int Notes, int FieldEdits)> items, int totalNotes, int totalFieldEdits)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, JsonWriterOptions);
        writer.WriteStartObject();
        writer.WritePropertyName("items");
        writer.WriteStartArray();
        foreach (var (id, notes, fieldEdits) in items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteNumber("notes", notes);
            writer.WriteNumber("fieldEdits", fieldEdits);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("totalNotes", totalNotes);
        writer.WriteNumber("totalFieldEdits", totalFieldEdits);
        writer.WriteNumber("totalItems", items.Count);
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    private void RenderInfoRecord(string kind, string message, int? itemId, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildInfoRecord(kind, message, itemId),
            _ => new RenderNode.Text(message, Severity.Info),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static RenderNode BuildInfoRecord(string kind, string message, int? itemId)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["message"] = RenderCell.String(message),
        };
        if (itemId.HasValue)
            fields["itemId"] = RenderCell.Integer(itemId.Value);
        return new RenderNode.Record(kind, fields);
    }

    private void RenderHumanSuccess(string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower == "minimal"
            ? new RenderNode.Text(message)
            : new RenderNode.Text(message, Severity.Success);
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private bool Confirm(string prompt, bool yes, string outputFormat)
    {
        if (yes) return true;
        Console.Write($"{prompt}? (y/N) ");
        var response = consoleInput.ReadLine();
        if (string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return true;
        RenderInfoRecord("discardCancelled", "Discard cancelled.", null, outputFormat);
        return false;
    }

    private static string FormatChangeSummary(int notes, int fieldEdits)
    {
        var parts = new List<string>(2);
        if (notes > 0) parts.Add($"{notes} note{(notes != 1 ? "s" : "")}");
        if (fieldEdits > 0) parts.Add($"{fieldEdits} field edit{(fieldEdits != 1 ? "s" : "")}");
        return parts.Count > 0 ? string.Join(" and ", parts) : "0 changes";
    }
}
