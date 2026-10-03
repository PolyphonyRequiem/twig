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

/// <summary>Reviews or resumes one exact all-worktree default family; never publishes, discards or releases claims.</summary>
internal sealed class ConnectionDefaultCommand(
    IConnectionDefaultTransitionService transitions,
    TwigConfiguration configuration,
    TwigPaths paths,
    OutputFormatterFactory formatters,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    public async Task<int> ExecuteAsync(string? binding, string? confirm,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (string.IsNullOrWhiteSpace(binding))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("--binding is required. Inspect 'twig connection list' for a binding belonging to the declared endpoint. No account fallback or force path exists."));
                return exit = 2;
            }
            var normalized = OutputFormats.Normalize(output);
            if (normalized is not ("human" or "json" or "json-full" or "json-compact" or "minimal"))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("Supported output formats: human, json, json-full, json-compact, minimal."));
                return exit = 2;
            }
            var result = confirm is null
                ? await transitions.PreviewAsync(configuration, paths, binding, ct).ConfigureAwait(false)
                : await transitions.ApplyAsync(configuration, paths, binding, confirm, ct).ConfigureAwait(false);
            if (normalized is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.ConnectionDefaultTransitionPreview));
            else if (normalized == "minimal")
            {
                Console.Out.WriteLine($"transition\t{result.State}\t{result.CanApply.ToString(CultureInfo.InvariantCulture)}\t{result.Digest}");
                Console.Out.WriteLine($"default\t{result.OriginalBindingId}\t{result.DesiredBindingId}\t{result.DefaultRevision?.ToString(CultureInfo.InvariantCulture)}");
                foreach (var member in result.Members)
                    Console.Out.WriteLine($"worktree\t{member.WorktreeRoot}\t{member.AttachmentRevision.ToString(CultureInfo.InvariantCulture)}\t{member.Affected.ToString(CultureInfo.InvariantCulture)}\t{member.BindingPin}\t{member.State}\t{member.Generation}");
                foreach (var blocker in result.Blockers) Console.Out.WriteLine("blocker\t" + blocker);
                foreach (var step in result.NextSteps) Console.Out.WriteLine("next\t" + step);
            }
            else
            {
                var rows = new List<RenderNode>
                {
                    new RenderNode.Text($"  connection: {result.ConnectionRef}"),
                    new RenderNode.Text($"  default: {result.OriginalBindingId ?? "(unavailable)"} → {result.DesiredBindingId}"),
                    new RenderNode.Text($"  default revision: {result.DefaultRevision}"),
                    new RenderNode.Text($"  digest: {result.Digest}"),
                };
                rows.AddRange(result.Members.Select(member => (RenderNode)new RenderNode.Text(
                    $"  {(member.Affected ? "affected" : "pinned, unchanged")}: {member.WorktreeRoot} (attachment rev {member.AttachmentRevision}, {member.State ?? "unchanged"})")));
                rows.AddRange(result.Blockers.Select(x => (RenderNode)new RenderNode.Text("  blocker: " + x)));
                rows.AddRange(result.NextSteps.Select(x => (RenderNode)new RenderNode.Hint(x)));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([
                    new RenderNode.Section($"Connection default: {result.State} ({(result.CanApply ? "eligible" : "blocked")})", rows)]));
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
            try { TelemetryHelper.TrackCommand(telemetry, "connection default", output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }
}
