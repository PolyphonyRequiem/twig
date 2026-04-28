using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig patch --json '{...}' [--id N] [--format markdown]</c>:
/// atomically patches multiple fields on a single work item via JSON input.
/// </summary>
public sealed class PatchCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IConsoleInput consoleInput,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory,
    ITelemetryClient? telemetryClient = null,
    IPromptStateWriter? promptStateWriter = null,
    TextReader? stdinReader = null,
    TextWriter? stderr = null,
    TextWriter? stdout = null)
{
    private readonly TextReader _stdin = stdinReader ?? Console.In;
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TextWriter _stdout = stdout ?? Console.Out;

    /// <summary>
    /// Execute an atomic multi-field patch on a single work item.
    /// </summary>
    /// <param name="json">JSON object with field name → value pairs.</param>
    /// <param name="readStdin">When true, read JSON from stdin instead of --json.</param>
    /// <param name="id">Target a specific work item by ID instead of the active item.</param>
    /// <param name="outputFormat">Output format: human, json, jsonc, minimal.</param>
    /// <param name="format">Convert values before sending. Supported: "markdown".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<int> ExecuteAsync(
        string? json = null,
        bool readStdin = false,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        string? format = null,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var (exitCode, fieldCount) = await ExecuteCoreAsync(json, readStdin, id, outputFormat, format, ct);
        TelemetryHelper.TrackCommand(
            telemetryClient,
            "patch",
            outputFormat,
            exitCode,
            startTimestamp,
            extraMetrics: new Dictionary<string, double>
            {
                ["field_count"] = fieldCount
            });
        return exitCode;
    }

    private async Task<(int ExitCode, int FieldCount)> ExecuteCoreAsync(
        string? json,
        bool readStdin,
        int? id,
        string outputFormat,
        string? format,
        CancellationToken ct)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var fieldCount = 0;

        // Validate input sources: exactly one of --json or --stdin must be specified
        var sourceCount = (json is not null ? 1 : 0) + (readStdin ? 1 : 0);
        if (sourceCount == 0)
        {
            _stderr.WriteLine(fmt.FormatError("No input specified. Provide --json '{...}' or --stdin."));
            return (2, fieldCount);
        }
        if (sourceCount > 1)
        {
            _stderr.WriteLine(fmt.FormatError("Multiple input sources. Use exactly one of: --json or --stdin."));
            return (2, fieldCount);
        }

        // Validate --format
        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            _stderr.WriteLine(fmt.FormatError($"Unknown format '{format}'. Supported formats: markdown"));
            return (2, fieldCount);
        }

        // Read JSON from the selected source
        var jsonInput = json ?? await _stdin.ReadToEndAsync(ct);

        // Parse JSON into field dictionary
        Dictionary<string, string>? fields;
        try
        {
            fields = JsonSerializer.Deserialize(jsonInput, TwigJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException ex)
        {
            _stderr.WriteLine(fmt.FormatError($"Invalid JSON: {ex.Message}"));
            return (2, fieldCount);
        }

        if (fields is null || fields.Count == 0)
        {
            _stderr.WriteLine(fmt.FormatError("JSON must be a non-empty object with field name → value pairs."));
            return (2, fieldCount);
        }

        fieldCount = fields.Count;

        // Build FieldChange[] with optional markdown conversion
        var changes = new List<FieldChange>(fields.Count);
        foreach (var (key, value) in fields)
        {
            var effectiveValue = format is not null
                ? MarkdownConverter.ToHtml(value)
                : value;
            changes.Add(new FieldChange(key, null, effectiveValue));
        }

        // Resolve the target work item
        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return (1, fieldCount);
        }

        // Fetch remote and resolve conflicts
        var remote = await adoService.FetchAsync(item.Id, ct);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return (1, fieldCount);
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return (0, fieldCount);

        // PATCH with conflict retry
        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(
                adoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            _stderr.WriteLine(fmt.FormatError("Concurrency conflict after retry. Run 'twig sync' and retry."));
            return (1, fieldCount);
        }

        // Auto-push pending notes
        await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);

        // Resync cache (non-fatal on failure)
        try
        {
            var updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(
                $"warning: Patch succeeded but cache may be stale — run 'twig sync' to resync ({ex.Message})");
        }

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        _stdout.WriteLine(fmt.FormatSuccess(
            $"#{item.Id} {item.Title}: patched {changes.Count} field(s)."));

        return (0, fieldCount);
    }
}
