using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig history &lt;id&gt;</c> (twig#241): an on-demand, read-only timeline of a
/// work item's revision history from the ADO Work Item Updates API.
/// </summary>
/// <remarks>
/// <para>
/// Read-only throughout — no workspace, cache, context, staged-change, or pending-change
/// mutation. History is never downloaded during sync and is never persisted.
/// </para>
/// <para>
/// JSON output is emitted via <see cref="WorkItemHistoryJsonWriter"/>, the single AOT-safe
/// writer shared with the <c>twig_history</c> MCP tool, so both surfaces emit an identical
/// document. Deliberately NOT routed through RenderTree: lossless arbitrary-JSON support there
/// is deferred (#250).
/// </para>
/// </remarks>
public sealed class HistoryCommand(
    IAdoWorkItemService adoService,
    CommandContext ctx)
{
    private readonly TextWriter _stderr = ctx.StderrWriter;

    /// <summary>Display the revision history for a work item.</summary>
    /// <param name="id">The work item ID (required).</param>
    /// <param name="detail">Comma-delimited update IDs, or <c>all</c>, to render in full detail.</param>
    /// <param name="field">Comma-delimited ADO field reference names to restrict deltas to.</param>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    public async Task<int> ExecuteAsync(
        int id,
        string? detail = null,
        string? field = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        int exitCode;

        try
        {
            exitCode = await ExecuteCoreAsync(id, detail, field, outputFormat, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Complete-or-error: auth, authorization, not-found, network, and malformed-response
            // conditions stay explicit errors and never degrade to an empty successful timeline.
            _stderr.WriteLine(fmt.FormatError($"Failed to read history for #{id}: {ex.Message}"));
            exitCode = 1;
        }

        TelemetryHelper.TrackCommand(
            ctx.TelemetryClient,
            "history",
            outputFormat,
            exitCode,
            startTimestamp,
            extraProperties: new Dictionary<string, string>
            {
                ["detailed"] = (detail is not null).ToString(),
                ["field_filtered"] = (field is not null).ToString(),
            });

        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(
        int id,
        string? detail,
        string? field,
        string outputFormat,
        CancellationToken ct)
    {
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);

        if (id <= 0)
        {
            _stderr.WriteLine(fmt.FormatError("A work item ID is required: twig history <id>."));
            return 1;
        }

        var parsed = WorkItemHistoryOptionsParser.Parse(detail, field);
        if (!parsed.IsSuccess)
        {
            _stderr.WriteLine(fmt.FormatError(parsed.Error));
            return 1;
        }

        var history = await adoService.FetchHistoryAsync(id, parsed.Value, ct);

        if (IsMachineFormat(outputFormat))
        {
            Console.WriteLine(WorkItemHistoryJsonWriter.Write(history));
            return 0;
        }

        RenderHuman(history);
        return 0;
    }

    internal static bool IsMachineFormat(string? outputFormat) =>
        (outputFormat ?? string.Empty).ToLowerInvariant()
            is "json" or "json-full" or "json-compact" or "minimal" or "ids";

    /// <summary>
    /// Chronological human rendering so history is readable at a terminal without parsing JSON.
    /// </summary>
    private static void RenderHuman(WorkItemHistory history)
    {
        Console.WriteLine($"History for #{history.WorkItemId} ({history.Events.Count} event(s), complete: {history.Complete})");

        if (history.Events.Count == 0)
        {
            Console.WriteLine("  (no matching updates)");
            return;
        }

        foreach (var evt in history.Events)
        {
            var when = evt.ChangedAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'") ?? "(unknown time)";
            var who = string.IsNullOrWhiteSpace(evt.ChangedBy) ? "(unknown)" : evt.ChangedBy;
            Console.WriteLine();
            Console.WriteLine($"  #{evt.UpdateId} (rev {evt.Revision})  {when}  {who}");

            if (evt.ChangedFields.Count > 0)
                Console.WriteLine($"    changed: {string.Join(", ", evt.ChangedFields)}");

            if (evt.Detailed)
            {
                foreach (var f in evt.Fields)
                    Console.WriteLine($"      {f.ReferenceName}: {Show(f.OldValue)} -> {Show(f.NewValue)}");
            }

            foreach (var relation in evt.Relations)
            {
                var verb = relation.Kind == RelationChangeKind.Added ? "+" : "-";
                var describe = relation.Target switch
                {
                    { Deleted: true } => $"#{relation.TargetId} (deleted)",
                    { Title: { } title } => $"#{relation.TargetId} '{title}'",
                    _ => $"#{relation.TargetId}",
                };
                Console.WriteLine($"    {verb} {relation.RelationType} -> {describe}");
            }
        }
    }

    private static string Show(string? value) => value is null ? "(none)" : $"'{value}'";
}
