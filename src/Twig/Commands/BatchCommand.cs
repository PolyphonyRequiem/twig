using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Content;

namespace Twig.Commands;

/// <summary>
/// Result of a batch operation across one or more work items.
/// </summary>
public sealed record BatchResult(
    IReadOnlyList<BatchItemResult> Items,
    int TotalFieldChanges,
    int TotalNotes);

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
    int FieldChangeCount);

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
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>
    /// Execute a batch of state transition, field updates, and/or note on a single work item.
    /// </summary>
    /// <param name="state">Target state name (e.g., Active, Closed).</param>
    /// <param name="set">Field updates as key=value pairs. Repeatable.</param>
    /// <param name="note">Comment text to add after the PATCH.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="ids">Comma-separated IDs for multi-item batch (reserved for future use).</param>
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

        // Resolve the target work item
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

        // Process single item
        var result = await ProcessItemAsync(item, state, fieldUpdates, note, fmt, outputFormat, ct);

        // Build BatchResult
        var batchResult = new BatchResult(
            new[] { result },
            result.FieldChangeCount,
            hasNote && result.Success ? 1 : 0);

        // Output
        if (result.Success)
        {
            var parts = new List<string>();
            if (result.NewState is not null)
                parts.Add($"{result.PreviousState} → {result.NewState}");
            if (result.FieldChangeCount > (result.NewState is not null ? 1 : 0))
                parts.Add($"{result.FieldChangeCount - (result.NewState is not null ? 1 : 0)} field(s) updated");
            if (hasNote)
                parts.Add("note added");

            var summary = string.Join(", ", parts);
            Console.WriteLine(fmt.FormatSuccess($"#{result.ItemId} {result.Title}: {summary}"));

            var hints = hintEngine.GetHints("batch",
                item: item,
                outputFormat: outputFormat,
                newStateName: result.NewState);
            foreach (var hint in hints)
            {
                var formatted = fmt.FormatHint(hint);
                if (!string.IsNullOrEmpty(formatted))
                    Console.WriteLine(formatted);
            }
        }
        else
        {
            _stderr.WriteLine(fmt.FormatError($"#{result.ItemId} {result.Title}: {result.Error}"));
            return 1;
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }

    private async Task<BatchItemResult> ProcessItemAsync(
        Domain.Aggregates.WorkItem item,
        string? state,
        List<(string Key, string Value)>? fieldUpdates,
        string? note,
        IOutputFormatter fmt,
        string outputFormat,
        CancellationToken ct)
    {
        var hasState = !string.IsNullOrWhiteSpace(state);

        // 1. Fetch remote
        var remote = await adoService.FetchAsync(item.Id, ct);

        // 2. Conflict resolution (interactive for single-item)
        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");

        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return new BatchItemResult(item.Id, item.Title, false, "Conflict detected (JSON emitted).", null, null, 0);
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return new BatchItemResult(item.Id, item.Title, false, "Conflict resolution cancelled.", null, null, 0);

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

                if (transition.RequiresConfirmation)
                {
                    var kind = transition.Kind == TransitionKind.Cut ? "REMOVE" : "move backward";
                    Console.Write($"This will {kind} #{item.Id} from '{item.State}' to '{resolvedState}'. Continue? [y/N] ");
                    var response = consoleInput.ReadLine()?.Trim();
                    if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine(fmt.FormatInfo("Cancelled."));
                        return new BatchItemResult(item.Id, item.Title, false, "Cancelled by user.", previousState, resolvedState, 0);
                    }
                }
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
        if (!string.IsNullOrWhiteSpace(note))
            await adoService.AddCommentAsync(item.Id, note, ct);

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
            FieldChangeCount: changes.Count);
    }
}
