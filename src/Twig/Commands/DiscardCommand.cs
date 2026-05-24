using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Formatters;
using Twig.Infrastructure.Services.Mutation;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig discard &lt;id&gt;</c> and <c>twig discard --all</c>:
/// drops pending changes (notes and field edits) for a single work item or all dirty items.
/// Seeds are excluded — use <c>twig seed discard</c> for seeds.
/// </summary>
/// <remarks>
/// Single-item discard delegates to <see cref="DiscardWorkflow"/>. The <c>--all</c>
/// flow stays here because it uses different store methods
/// (<c>ClearAllChangesAsync</c>, <c>ClearPhantomDirtyFlagsAsync</c>) and operates
/// across the workspace rather than on one item.
/// </remarks>
public sealed class DiscardCommand(
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    DiscardWorkflow discardWorkflow,
    IPromptStateWriter? promptStateWriter = null,
    ITelemetryClient? telemetryClient = null)
{
    private static readonly JsonWriterOptions JsonWriterOptions = new() { Indented = true };

    /// <summary>Discard pending changes for a single item or all dirty items.</summary>
    /// <param name="id">The work item ID whose pending changes to discard.</param>
    /// <param name="all">When true, discard all pending changes for non-seed items.</param>
    /// <param name="yes">When true, skip the confirmation prompt.</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
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

    // ── Single-item flow ────────────────────────────────────────────

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

        // Seed guard — seeds use 'twig seed discard'
        if (item.IsSeed)
        {
            Console.Error.WriteLine(fmt.FormatError($"#{itemId} is a seed. Use 'twig seed discard {itemId}' instead."));
            return (1, 0);
        }

        var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(itemId, ct);

        // Pre-confirm peek for change summary text (workflow re-queries, which is
        // acceptable since the store is local-only and cheap).
        if (item.IsDirty || notes > 0 || fieldEdits > 0)
        {
            if (notes > 0 || fieldEdits > 0)
            {
                var summary = FormatChangeSummary(notes, fieldEdits);
                if (!Confirm($"Discard {summary} for #{itemId} '{item.Title}'", yes, fmt))
                    return (0, 0);
            }
        }

        var outcome = await discardWorkflow.ExecuteAsync(item, ct);

        switch (outcome)
        {
            case DiscardOutcome.NoChanges:
                Console.WriteLine(fmt.FormatInfo($"#{itemId} '{item.Title}' has no pending changes."));
                return (0, 0);

            case DiscardOutcome.PhantomDirtyCleared phantom:
                Console.WriteLine(fmt.FormatInfo($"#{itemId} '{item.Title}' had a stale dirty flag (cleared)."));
                foreach (var w in phantom.Warnings) Console.Error.WriteLine(w);
                return (0, 0);

            case DiscardOutcome.Discarded discarded:
                var summaryText = FormatChangeSummary(discarded.NotesCount, discarded.FieldEditsCount);
                if (outputFormat.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                    WriteJson([(itemId, discarded.NotesCount, discarded.FieldEditsCount)], discarded.NotesCount, discarded.FieldEditsCount);
                else
                    Console.WriteLine(fmt.FormatSuccess($"Discarded {summaryText} for #{itemId} '{item.Title}'."));
                foreach (var w in discarded.Warnings) Console.Error.WriteLine(w);
                return (0, 1);

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled DiscardOutcome: {outcome.GetType().Name}");
        }
    }

    // ── All-items flow ──────────────────────────────────────────────

    private async Task<(int ExitCode, int ItemCount)> ExecuteAllAsync(
        IOutputFormatter fmt,
        bool yes,
        string outputFormat,
        CancellationToken ct)
    {
        var dirtyItems = await workItemRepo.GetDirtyItemsAsync(ct);

        // Exclude seeds — they are managed by 'twig seed discard'
        var nonSeedDirty = new List<(int Id, int Notes, int FieldEdits)>();
        foreach (var item in dirtyItems)
        {
            if (item.IsSeed) continue;
            var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(item.Id, ct);
            nonSeedDirty.Add((item.Id, notes, fieldEdits));
        }

        if (nonSeedDirty.Count == 0)
        {
            // Also clean up phantom-dirty flags while we're here
            await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);
            Console.WriteLine(fmt.FormatInfo("No pending changes to discard."));
            return (0, 0);
        }

        var totalNotes = nonSeedDirty.Sum(x => x.Notes);
        var totalFieldEdits = nonSeedDirty.Sum(x => x.FieldEdits);
        var totalItems = nonSeedDirty.Count;
        var aggregateSummary = $"{totalItems} item{(totalItems != 1 ? "s" : "")} ({FormatChangeSummary(totalNotes, totalFieldEdits)})";

        if (!Confirm($"Discard all pending changes for {aggregateSummary}", yes, fmt))
            return (0, 0);

        // Bulk clear: pending changes then atomic dirty-flag cleanup
        await pendingChangeStore.ClearAllChangesAsync(ct);
        await workItemRepo.ClearPhantomDirtyFlagsAsync(ct);

        // Report
        if (outputFormat.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            WriteJson(nonSeedDirty, totalNotes, totalFieldEdits);
        else
            Console.WriteLine(fmt.FormatSuccess($"Discarded all pending changes for {aggregateSummary}."));

        // DD-9: Update prompt state after successful discard
        if (promptStateWriter is not null)
            await promptStateWriter.WritePromptStateAsync();

        return (0, nonSeedDirty.Count);
    }

    // ── JSON output ─────────────────────────────────────────────────

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

    // ── Helpers──────────────────────────────────────────────────────

    private bool Confirm(string prompt, bool yes, IOutputFormatter fmt)
    {
        if (yes) return true;
        Console.Write($"{prompt}? (y/N) ");
        var response = consoleInput.ReadLine();
        if (string.Equals(response?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return true;
        Console.WriteLine(fmt.FormatInfo("Discard cancelled."));
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
