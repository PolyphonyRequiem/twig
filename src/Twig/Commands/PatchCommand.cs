using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Services.Mutation;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig patch --json '{...}' [--id N] [--format markdown|raw]</c>:
/// atomically patches multiple fields on a single work item via JSON input.
/// HTML-typed fields default to Markdown→HTML conversion; pass <c>--format raw</c>
/// to send pre-rendered HTML or to suppress conversion on plain-text fields.
/// </summary>
/// <remarks>
/// Migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: success output is built as a <see cref="RenderTree.RenderTree"/>
/// per output format. <see cref="OutputFormatterFactory"/> is retained only for
/// stderr error formatting (matches slices 7-10).
/// </remarks>
public sealed class PatchCommand(
    ActiveItemResolver activeItemResolver,
    IAdoWorkItemService adoService,
    IConsoleInput consoleInput,
    IWorkItemRepository workItemRepo,
    IFieldDefinitionStore fieldDefStore,
    PatchWorkflow patchWorkflow,
    OutputFormatterFactory formatterFactory,
    ITelemetryClient? telemetryClient = null,
    TextReader? stdinReader = null,
    TextWriter? stderr = null,
    TextWriter? stdout = null,
    RendererFactory? rendererFactory = null)
{
    private readonly TextReader _stdin = stdinReader ?? Console.In;
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TextWriter _stdout = stdout ?? Console.Out;
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

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
        using var scope = new CommandActivityScope("patch", outputFormat);
        int exitCode;
        try
        {
            int fieldCount;
            (exitCode, fieldCount) = await ExecuteCoreAsync(json, readStdin, id, outputFormat, format, ct);
            scope.Complete(exitCode);
            TelemetryHelper.TrackCommand(
                telemetryClient,
                "patch",
                outputFormat,
                exitCode,
                scope.StartTimestamp,
                extraMetrics: new Dictionary<string, double>
                {
                    ["field_count"] = fieldCount
                });
            return exitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.Fail(ex);
            throw;
        }
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
        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
        {
            _stderr.WriteLine(fmt.FormatError(formatError));
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

        // Resolve effective values per-field. Auto mode (format == null) defers to
        // the field definition store: HTML-typed fields get Markdown→HTML conversion;
        // plain-text fields are passed through unchanged.
        var changes = new List<FieldChange>(fields.Count);
        var warnedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            var fieldResolution = await HtmlFieldFormatter.ResolveAsync(
                key, value, format, fieldDefStore,
                onMissingFieldDef: name =>
                {
                    if (warnedFields.Add(name))
                        _stderr.WriteLine($"warning: field type unknown for '{name}'; not converting. Use --format markdown to force conversion.");
                },
                ct);
            changes.Add(new FieldChange(key, null, fieldResolution.EffectiveValue));
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

        // Seed path: workflow handles seed routing entirely.
        if (item.IsSeed)
        {
            var seedOutcome = await patchWorkflow.ExecuteAsync(item, changes, remote: null, ct);
            return RenderOutcome(seedOutcome, item, fmt, fieldCount, outputFormat);
        }

        // Non-seed: fetch remote, run conflict-resolution UI, then call workflow.
        var remote = await adoService.FetchAsync(item.Id, ct);

        var conflictOutcome = await ConflictResolutionFlow.ResolveAsync(
            item, remote, fmt, outputFormat, consoleInput, workItemRepo,
            $"#{item.Id} updated from remote.");
        if (conflictOutcome == ConflictOutcome.ConflictJsonEmitted)
            return (1, fieldCount);
        if (conflictOutcome is ConflictOutcome.AcceptedRemote or ConflictOutcome.Aborted)
            return (0, fieldCount);

        var outcome = await patchWorkflow.ExecuteAsync(item, changes, remote, ct);
        return RenderOutcome(outcome, item, fmt, fieldCount, outputFormat);
    }

    private (int ExitCode, int FieldCount) RenderOutcome(
        PatchOutcome outcome, Domain.Aggregates.WorkItem item, IOutputFormatter fmt, int fieldCount, string outputFormat)
    {
        switch (outcome)
        {
            case PatchOutcome.SeedPatched s:
                RenderPatched(s.Item.Id, s.Item.Title, s.Changes.Count, wasSeed: true, outputFormat);
                foreach (var w in s.Warnings) _stderr.WriteLine($"warning: {w}");
                return (0, fieldCount);
            case PatchOutcome.SeedFieldRejected r:
                _stderr.WriteLine(fmt.FormatError($"Field '{r.FieldName}' failed: {r.Reason}"));
                return (1, fieldCount);
            case PatchOutcome.Patched p:
                RenderPatched(p.UpdatedItem.Id, p.UpdatedItem.Title, p.Changes.Count, wasSeed: false, outputFormat);
                foreach (var w in p.Warnings) _stderr.WriteLine($"warning: {w}");
                return (0, fieldCount);
            case PatchOutcome.ConflictAfterRetry:
                _stderr.WriteLine(fmt.FormatError("Concurrency conflict after retry. Run 'twig sync' and retry."));
                return (1, fieldCount);
            case PatchOutcome.AdoUnreachable a:
                _stderr.WriteLine(fmt.FormatError($"ADO call failed: {a.Reason}"));
                return (1, fieldCount);
            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled PatchOutcome: {outcome.GetType().Name}");
        }
    }

    private void RenderPatched(int itemId, string title, int fieldCount, bool wasSeed, string outputFormat)
    {
        var message = $"#{itemId} {title}: patched {fieldCount} field(s).";
        var tree = BuildPatchedTree(itemId, title, fieldCount, wasSeed, message, outputFormat);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static RenderTree.RenderTree BuildPatchedTree(int itemId, string title, int fieldCount, bool wasSeed, string message, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                BuildFieldsPatchedRecord(itemId, title, fieldCount, wasSeed, message),
            _ => new RenderNode.Text(message, Severity.Success),
        };
        return new RenderTree.RenderTree(new[] { node });
    }

    private static RenderNode BuildFieldsPatchedRecord(int itemId, string title, int fieldCount, bool wasSeed, string message)
    {
        var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = new RenderCell(itemId.ToString(), new RenderValue.Integer(itemId)),
            ["title"] = new RenderCell(title, new RenderValue.String(title)),
            ["fieldCount"] = new RenderCell(fieldCount.ToString(), new RenderValue.Integer(fieldCount)),
            ["wasSeed"] = new RenderCell(wasSeed ? "true" : "false", new RenderValue.Boolean(wasSeed)),
            ["message"] = new RenderCell(message, new RenderValue.String(message)),
        };
        return new RenderNode.Record("fieldsPatched", fields);
    }
}
