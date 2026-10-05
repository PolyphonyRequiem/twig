using System.Diagnostics;
using System.Globalization;
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

/// <summary>Deliberate checkout-local selection changes; preview never publishes or discards work.</summary>
internal sealed class ConnectionPinCommand(
    IConnectionBindingTransitionService transitions,
    TwigConfiguration configuration,
    TwigPaths paths,
    OutputFormatterFactory formatters,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    public async Task<int> ExecuteAsync(string? binding, bool remove, string? confirm,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var command = remove ? "connection unpin" : "connection pin";
        try
        {
            if (!remove && string.IsNullOrWhiteSpace(binding))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("--binding is required. Inspect 'twig connection list' for registered binding IDs; use 'twig connection unpin' to remove the local pin."));
                return exit = 2;
            }
            var normalized = OutputFormats.Normalize(output);
            if (normalized is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("Supported output formats: human, json, json-full, json-compact, minimal."));
                return exit = 2;
            }
            var desired = remove ? null : binding;
            var result = confirm is null
                ? await transitions.PreviewAsync(configuration, paths, desired, ct).ConfigureAwait(false)
                : await transitions.ApplyAsync(configuration, paths, desired, confirm, ct).ConfigureAwait(false);
            if (normalized is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.ConnectionBindingTransitionPreview));
            else if (normalized == "minimal")
            {
                Console.Out.WriteLine($"transition\t{result.State}\t{result.CanApply.ToString(CultureInfo.InvariantCulture)}\t{result.Digest}");
                Console.Out.WriteLine($"selection\t{result.OriginalBindingId}\t{result.DesiredBindingId}\t{result.AttachmentRevision.ToString(CultureInfo.InvariantCulture)}");
                foreach (var blocker in result.Blockers) Console.Out.WriteLine("blocker\t" + blocker);
                foreach (var step in result.NextSteps) Console.Out.WriteLine("next\t" + step);
            }
            else
            {
                var rows = new List<RenderNode>
                {
                    new RenderNode.Text($"  checkout: {result.WorktreeRoot}"),
                    new RenderNode.Text($"  selection: {result.OriginalBindingId ?? "(unbound)"} → {result.DesiredBindingId ?? "(default unavailable)"}"),
                    new RenderNode.Text($"  attachment revision: {result.AttachmentRevision}"),
                    new RenderNode.Text($"  digest: {result.Digest}"),
                };
                rows.AddRange(result.Blockers.Select(x => (RenderNode)new RenderNode.Text("  blocker: " + x)));
                rows.AddRange(result.NextSteps.Select(x => (RenderNode)new RenderNode.Hint(x)));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([
                    new RenderNode.Section($"Checkout binding: {result.State} ({(result.CanApply ? "eligible" : "blocked")})", rows)]));
            }
            return exit = result.CanApply ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or AdoAuthenticationException)
        {
            Console.Error.WriteLine(formatters.GetFormatter(output).FormatError(ex.Message));
            return exit = 1;
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetry, command, output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }
}
