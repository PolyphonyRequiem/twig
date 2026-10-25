using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Twig.Browser;
using Twig.Formatters;
using Twig.Infrastructure.Serialization;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Formats one native browser observation without owning admission or storage.</summary>
internal sealed class BenchDetailCommand(CommandContext ctx, BrowserReadModule browser, RendererFactory renderers)
{
    // Explicit-Bench refusals always lead with a stable machine code the extension can branch on.
    private static string Coded(BrowserReadFailure failure)
    {
        if (failure.Message.StartsWith("bench-", StringComparison.Ordinal)) return failure.Message;
        var code = failure.Kind switch
        {
            BrowserReadFailureKind.Usage => "bench-read-usage",
            BrowserReadFailureKind.AuthorityInvalidated => "bench-origin-changed",
            _ => "bench-read-unavailable",
        };
        return $"{code}: {failure.Message}";
    }

    internal async Task<int> ExecuteAsync(int id, int width = 80,
        string output = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        bool sync = false, CancellationToken ct = default, bool document = false, string? bench = null)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (output.Equals("ids", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A single-item detail supports human, json, and minimal output, not ids.");
            var json = output.ToLowerInvariant() is "json" or "json-full" or "json-compact";
            if (bench is not null)
            {
                if (string.IsNullOrWhiteSpace(bench)) throw new ArgumentException("bench-read-usage: --bench requires a saved Bench name or id.");
                if (!document || !json) throw new ArgumentException("bench-read-usage: --bench requires --document and JSON output (-o json); it is not supported by other detail modes.");
                if (sync) throw new ArgumentException("bench-read-usage: --bench reads the cache only; --sync does not apply.");
            }
            if (document && !json)
                throw new ArgumentException("--document requires JSON output (-o json). Without --bench it may read server form metadata; item refresh still requires --sync.");
            var ansi = !output.Equals("minimal", StringComparison.OrdinalIgnoreCase) && (json || !Console.IsOutputRedirected);
            var result = await browser.ReadDetailAsync(new(id, width, ansi, expectBench, expectBinding, expectIdentity, sync, Document: document, ExplicitBench: bench), ct);
            if (result.Failure is { } failure)
            {
                if (failure.Kind == BrowserReadFailureKind.Canceled) throw new OperationCanceledException(failure.Message, ct);
                exit = failure.Kind == BrowserReadFailureKind.Usage ? 2 : 1;
                CommandError.Write(renderers, ctx.StderrWriter, output, bench is null ? failure.Message : Coded(failure));
                return exit;
            }
            var observation = result.Observation!;
            if (document)
                Console.WriteLine(JsonSerializer.Serialize(
                    observation.Document ?? throw new InvalidOperationException("Semantic detail was not captured."),
                    TwigJsonContext.Default.CanvasDetailExport));
            else if (json)
            {
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([new RenderNode.Record("benchDetail",
                    new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["version"] = RenderCell.Integer(1),
                        ["benchId"] = RenderCell.String(observation.Capture.BenchId),
                        ["benchName"] = RenderCell.String(observation.Capture.BenchName),
                        ["bindingId"] = RenderCell.String(observation.Capture.BindingId),
                        ["identityId"] = RenderCell.String(observation.Capture.IdentityId),
                        ["workItemId"] = RenderCell.Integer(observation.WorkItemId),
                        ["title"] = RenderCell.String(observation.Title),
                        ["ansi"] = RenderCell.String(observation.InitialPresentation.Ansi),
                    })]));
            }
            else
                Console.Write(observation.InitialPresentation.Ansi);
            exit = 0;
            return exit;
        }
        catch (ArgumentException ex)
        {
            exit = 2;
            CommandError.Write(renderers, ctx.StderrWriter, output, ex.Message);
            return exit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CommandError.Write(renderers, ctx.StderrWriter, output, ex.Message);
            return exit;
        }
        finally
        {
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "bench-detail",
                ["output_format"] = output,
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }
}
