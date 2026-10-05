using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Inspect native uncertain writes and reconcile only attributable original-actor evidence.</summary>
internal sealed class ConnectionWritesCommand(
    IConnectionRemoteWriteReconciliationService reconciliation,
    TwigConfiguration configuration,
    TwigPaths paths,
    OutputFormatterFactory formatters,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    public async Task<int> ExecuteAsync(bool reconcile, string? intent = null, string? confirm = null,
        string? authorize = null, string? rationale = null,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (reconcile && (string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(confirm)
                || string.IsNullOrWhiteSpace(authorize) || string.IsNullOrWhiteSpace(rationale)))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("--intent, --confirm, --authorize and --rationale are required. Inspect 'twig connection writes' first; only native original-actor evidence can settle an uncertain write."));
                return exit = 2;
            }
            var normalized = OutputFormats.Normalize(output);
            if (normalized is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("Supported output formats: human, json, json-full, json-compact, minimal."));
                return exit = 2;
            }
            IReadOnlyList<ConnectionRemoteWriteInspection> reports = reconcile
                ? [await reconciliation.ReconcileAsync(configuration, paths, intent!, confirm!, authorize!, rationale!, ct).ConfigureAwait(false)]
                : await reconciliation.InspectAsync(configuration, paths, ct).ConfigureAwait(false);
            if (normalized is "json" or "json-full" or "json-compact")
            {
                if (reconcile)
                    Console.Out.WriteLine(JsonSerializer.Serialize(reports[0], TwigJsonContext.Default.ConnectionRemoteWriteInspection));
                else
                    Console.Out.WriteLine(JsonSerializer.Serialize(reports.ToArray(), TwigJsonContext.Default.ConnectionRemoteWriteInspectionArray));
            }
            else if (normalized == "minimal")
            {
                foreach (var report in reports)
                {
                    Console.Out.WriteLine($"write\t{report.IntentId}\t{report.State}\t{report.Digest}\t{report.IdentityId}");
                    foreach (var blocker in report.Blockers) Console.Out.WriteLine($"blocker\t{report.IntentId}\t{blocker}");
                    foreach (var step in report.NextSteps) Console.Out.WriteLine($"next\t{report.IntentId}\t{step}");
                }
            }
            else
            {
                var sections = new List<RenderNode>();
                foreach (var report in reports)
                {
                    var rows = new List<RenderNode>
                    {
                        new RenderNode.Text($"  original identity: {report.IdentityId}"),
                        new RenderNode.Text($"  binding: {report.BindingId}"),
                        new RenderNode.Text($"  effect: {report.Request.EffectKind}"),
                        new RenderNode.Text($"  digest: {report.Digest}"),
                    };
                    rows.AddRange(report.Blockers.Select(x => (RenderNode)new RenderNode.Text("  blocker: " + x)));
                    rows.AddRange(report.NextSteps.Select(x => (RenderNode)new RenderNode.Hint(x)));
                    sections.Add(new RenderNode.Section($"Native write {report.IntentId}: {report.State}", rows));
                }
                if (reports.Count == 0) sections.Add(new RenderNode.Text("No native remote-write intents are recorded for this checkout."));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree(sections));
            }
            return exit = !reconcile || reports[0].Receipt is not null ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or AdoException)
        {
            Console.Error.WriteLine(formatters.GetFormatter(output).FormatError(ex.Message));
            return exit = 1;
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, reconcile ? "connection reconcile-write" : "connection writes", output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }
}
