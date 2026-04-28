using System.Text;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;

namespace Twig.Commands;

/// <summary>
/// Per-item result within a batch operation.
/// </summary>
public sealed record BatchItemResult(
    int ItemId,
    string Title,
    bool Success,
    string? Error,
    string? PreviousState,
    string? NewState,
    int FieldChangeCount,
    bool NoteAdded = false);

/// <summary>
/// Implements <c>twig batch</c>: combines state transitions, field updates, and notes
/// into a single CLI invocation with one fetch, one PATCH, and one cache resync per item.
/// </summary>
public sealed class BatchCommand(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    IConsoleInput consoleInput,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IPromptStateWriter? promptStateWriter = null,
    TextWriter? stdout = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stdout = stdout ?? Console.Out;
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Execute a batch of state transition, field updates, and/or note on one or more work items.
    /// </summary>
    /// <param name="state">Target state name (e.g., Active, Closed).</param>
    /// <param name="set">Field updates as key=value pairs. Repeatable.</param>
    /// <param name="note">Comment text to add after the PATCH.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="ids">Comma-separated IDs for multi-item batch.</param>
    /// <param name="outputFormat">Output format: human, json, jsonc, minimal.</param>
    /// <param name="format">Convert --set values before sending. Supported: "markdown".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int> ExecuteAsync(
        string? state = null,
        string[]? set = null,
        string? note = null,
        int? id = null,
        string? ids = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        string? format = null,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // Validate: --id and --ids are mutually exclusive
        if (id.HasValue && !string.IsNullOrWhiteSpace(ids))
        {
            _stderr.WriteLine(fmt.FormatError("--id and --ids are mutually exclusive."));
            return 2;
        }

        // Validate: at least one operation must be specified
        var hasState = !string.IsNullOrWhiteSpace(state);
        var hasFields = set is { Length: > 0 };
        var hasNote = !string.IsNullOrWhiteSpace(note);

        if (!hasState && !hasFields && !hasNote)
        {
            _stderr.WriteLine(fmt.FormatError(
                "At least one of --state, --set, or --note must be specified."));
            return 2;
        }

        // Validate --format
        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            _stderr.WriteLine(fmt.FormatError($"Unknown format '{format}'. Supported formats: markdown"));
            return 2;
        }

        // Parse --set key=value pairs (split on first '=' only)
        List<(string Key, string Value)>? fieldUpdates = null;
        if (hasFields)
        {
            fieldUpdates = new List<(string, string)>(set!.Length);
            foreach (var pair in set)
            {
                var eqIndex = pair.IndexOf('=');
                if (eqIndex < 1)
                {
                    _stderr.WriteLine(fmt.FormatError($"Invalid --set format: '{pair}'. Expected key=value."));
                    return 2;
                }

                var key = pair[..eqIndex];
                var rawValue = pair[(eqIndex + 1)..];
                var effectiveValue = format is not null
                    ? MarkdownConverter.ToHtml(rawValue)
                    : rawValue;
                fieldUpdates.Add((key, effectiveValue));
            }
        }

        // Parse --ids into list of target IDs
        var parsedIds = ParseIds(ids);
        if (parsedIds is not null && parsedIds.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError("--ids must contain valid comma-separated integer IDs."));
            return 2;
        }

        // Multi-item mode (--ids)
        if (parsedIds is not null)
            return await ExecuteMultiItemAsync(parsedIds, state, fieldUpdates, note, fmt, outputFormat, ct);

        // Single-item mode (--id or active item)
        return await ExecuteSingleItemAsync(id, state, fieldUpdates, note, fmt, outputFormat, ct);
    }

    /// <summary>Parses comma-separated IDs; returns null if input is empty, empty list if malformed.</summary>
    private static List<int>? ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return null;

        var result = new List<int>();
        foreach (var segment in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(segment, out var parsed))
                return new List<int>(); // Return empty to signal parse failure
            result.Add(parsed);
        }

        return result;
    }

    private async Task<int> ExecuteSingleItemAsync(
        int? id,
        string? state,
        List<(string Key, string Value)>? fieldUpdates,
        string? note,
        IOutputFormatter fmt,
        string outputFormat,
        CancellationToken ct)
    {
        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var item, out var errorId, out var errorReason))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        var result = await ProcessItemAsync(item, state, fieldUpdates, note, interactive: true, fmt, outputFormat, ct);

        if (result.Success)
        {
            if (result.NewState is not null || result.FieldChangeCount > 0 || result.NoteAdded)
                RenderItemSuccess(result, fmt);
            RenderHints(item, outputFormat, result.NewState, fmt);
        }
        else
        {
            _stderr.WriteLine(fmt.FormatError($"#{result.ItemId} {result.Title}: {result.Error}"));
            return 1;
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    private async Task<int> ExecuteMultiItemAsync(
        List<int> itemIds,
        string? state,
        List<(string Key, string Value)>? fieldUpdates,
        string? note,
        IOutputFormatter fmt,
        string outputFormat,
        CancellationToken ct)
    {
        var results = new List<BatchItemResult>(itemIds.Count);

        foreach (var itemId in itemIds)
        {
            var resolved = await activeItemResolver.ResolveByIdAsync(itemId, ct);
            if (!resolved.TryGetWorkItem(out var item, out _, out _))
            {
                results.Add(new BatchItemResult(itemId, string.Empty, false,
                    $"Work item #{itemId} not found.", null, null, 0));
                continue;
            }

            try
            {
                var result = await ProcessItemAsync(item, state, fieldUpdates, note, interactive: false, fmt, outputFormat, ct);
                results.Add(result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new BatchItemResult(itemId, item.Title, false, ex.Message, null, null, 0));
            }
        }

        var succeeded = results.Count(r => r.Success);
        var failed = results.Count - succeeded;

        // FR-10: JSON output produces structured BatchResult
        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            _stdout.WriteLine(FormatBatchResultJson(results, succeeded, failed));
        }
        else
        {
            // Render per-item results (human/minimal/jsonc — jsonc intentionally uses human-readable output)
            foreach (var result in results)
            {
                if (result.Success)
                {
                    if (result.NewState is not null || result.FieldChangeCount > 0 || result.NoteAdded)
                        RenderItemSuccess(result, fmt);
                }
                else
                    _stderr.WriteLine(fmt.FormatError($"#{result.ItemId} {(result.Title.Length > 0 ? result.Title + ": " : "")}{result.Error}"));
            }

            if (failed > 0)
                _stderr.WriteLine(fmt.FormatError($"Batch complete: {succeeded}/{results.Count} succeeded, {failed} failed."));
            else
                _stdout.WriteLine(fmt.FormatSuccess($"Batch complete: {succeeded}/{results.Count} succeeded."));
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        // TODO: Emit telemetry — operation count, item count, duration (NFR-3)

        return failed > 0 ? 1 : 0;
    }

    private void RenderItemSuccess(BatchItemResult result, IOutputFormatter fmt)
    {
        var parts = new List<string>();
        if (result.NewState is not null)
            parts.Add($"{result.PreviousState} → {result.NewState}");
        var nonStateFields = result.FieldChangeCount - (result.NewState is not null ? 1 : 0);
        if (nonStateFields > 0)
            parts.Add($"{nonStateFields} field(s) updated");
        if (result.NoteAdded)
            parts.Add("note added");

        var summary = string.Join(", ", parts);
        _stdout.WriteLine(fmt.FormatSuccess($"#{result.ItemId} {result.Title}: {summary}"));
    }

    private void RenderHints(Domain.Aggregates.WorkItem item, string outputFormat, string? newStateName, IOutputFormatter fmt)
    {
        var hints = hintEngine.GetHints("batch",
            item: item,
            outputFormat: outputFormat,
            newStateName: newStateName);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                _stdout.WriteLine(formatted);
        }
    }

    /// <summary>Formats BatchResult as structured JSON using Utf8JsonWriter (AOT-safe).</summary>
    private static string FormatBatchResultJson(List<BatchItemResult> items, int succeeded, int failed)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("totalItems", items.Count);
        writer.WriteNumber("succeeded", succeeded);
        writer.WriteNumber("failed", failed);
        writer.WriteNumber("totalFieldChanges", items.Where(r => r.Success).Sum(r => r.FieldChangeCount));
        writer.WriteNumber("totalNotes", items.Count(r => r.NoteAdded));

        writer.WriteStartArray("items");
        foreach (var item in items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("itemId", item.ItemId);
            writer.WriteString("title", item.Title);
            writer.WriteBoolean("success", item.Success);
            if (item.Error is not null) writer.WriteString("error", item.Error);
            if (item.PreviousState is not null) writer.WriteString("previousState", item.PreviousState);
            if (item.NewState is not null) writer.WriteString("newState", item.NewState);
            writer.WriteNumber("fieldChangeCount", item.FieldChangeCount);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<BatchItemResult> ProcessItemAsync(
        Domain.Aggregates.WorkItem item,
        string? state,
        List<(string Key, string Value)>? fieldUpdates,
        string? note,
        bool interactive,
        IOutputFormatter fmt,
        string outputFormat,
        CancellationToken ct)
    {
        var hasState = !string.IsNullOrWhiteSpace(state);

        // 1. Fetch remote
        var remote = await adoService.FetchAsync(item.Id, ct);

        // 2. Conflict resolution
        if (interactive)
        {
            // Single-item: interactive conflict resolution
            var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
                item, remote, fmt, outputFormat, consoleInput, workItemRepo,
                $"#{item.Id} updated from remote.");

            if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
                return new BatchItemResult(item.Id, item.Title, false, "Conflict detected (JSON emitted).", null, null, 0);
            // AcceptedRemote and Aborted are resolved non-error outcomes (consistent with
            // StateCommand/UpdateCommand returning exit 0 for these cases).
            if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
                return new BatchItemResult(item.Id, item.Title, true, null, null, null, 0);
        }
        else
        {
            // Multi-item: auto-accept-remote on conflict (DD-3)
            var mergeResult = ConflictResolver.Resolve(item, remote);
            if (mergeResult is MergeResult.HasConflicts)
            {
                await workItemRepo.SaveAsync(remote, ct);
            }
            // AutoMergeable: step-8 resync will update the cache
        }

        // 3. State validation (if --state specified)
        string? resolvedState = null;
        string? previousState = null;
        if (hasState)
        {
            var processConfig = processConfigProvider.GetConfiguration();
            if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
                return new BatchItemResult(item.Id, item.Title, false,
                    $"No process configuration found for type '{item.Type}'.", null, null, 0);

            var resolveResult = StateResolver.ResolveByName(state!, typeConfig.StateEntries);
            if (!resolveResult.IsSuccess)
                return new BatchItemResult(item.Id, item.Title, false, resolveResult.Error, null, null, 0);

            resolvedState = resolveResult.Value;

            if (string.Equals(item.State, resolvedState, StringComparison.OrdinalIgnoreCase))
            {
                // Already in target state — skip state change but continue with field updates
                resolvedState = null;
            }
            else
            {
                previousState = item.State;
                var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, resolvedState);

                if (!transition.IsAllowed)
                    return new BatchItemResult(item.Id, item.Title, false,
                        $"Transition from '{item.State}' to '{resolvedState}' is not allowed.", null, null, 0);

            }
        }

        // 4. Build combined FieldChange[]
        var changes = new List<FieldChange>();
        if (resolvedState is not null)
            changes.Add(new FieldChange("System.State", previousState, resolvedState));

        if (fieldUpdates is not null)
        {
            foreach (var (key, value) in fieldUpdates)
                changes.Add(new FieldChange(key, null, value));
        }

        // 5. PATCH (only if there are field changes)
        if (changes.Count > 0)
        {
            try
            {
                await ConflictRetryHelper.PatchWithRetryAsync(
                    adoService, item.Id, changes, remote.Revision, ct);
            }
            catch (AdoConflictException)
            {
                return new BatchItemResult(item.Id, item.Title, false,
                    "Concurrency conflict after retry. Run 'twig sync' and retry.",
                    previousState, resolvedState, 0);
            }
        }

        // 6. Add comment (if --note specified)
        var noteAdded = false;
        if (!string.IsNullOrWhiteSpace(note))
        {
            await adoService.AddCommentAsync(item.Id, note, ct);
            noteAdded = true;
        }

        // 7. Auto-push residual pending notes
        await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);

        // 8. Resync cache (non-fatal on failure)
        try
        {
            var updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(
                $"warning: Batch operation succeeded but cache may be stale — run 'twig sync' to resync ({ex.Message})");
        }

        return new BatchItemResult(
            item.Id,
            item.Title,
            Success: true,
            Error: null,
            PreviousState: previousState,
            NewState: resolvedState,
            FieldChangeCount: changes.Count,
            NoteAdded: noteAdded);
    }
}
