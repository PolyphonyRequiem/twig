using System.Diagnostics;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Explicit migration administration, available before attachment without admitting normal work.</summary>
internal sealed class ConnectionMigrateCommand(
    IConnectionMigrationService migration,
    TwigConfiguration configuration,
    TwigPaths paths,
    OutputFormatterFactory formatters,
    RendererFactory renderers,
    ITelemetryClient? telemetry = null)
{
    public async Task<int> ExecuteAsync(string? identity, string? method, string? confirm,
        string output = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (method is not null && method is not ("aad" or "pat"))
            {
                Console.Error.WriteLine(formatters.GetFormatter(output).FormatError("--method must be aad or pat."));
                return exit = 2;
            }
            var result = confirm is null
                ? await migration.PreviewAsync(configuration, paths, identity, method, ct).ConfigureAwait(false)
                : await migration.ApplyAsync(configuration, paths, identity, method, confirm, ct).ConfigureAwait(false);
            if (OutputFormats.Normalize(output) is "json" or "json-full" or "json-compact")
                Console.Out.WriteLine(JsonSerializer.Serialize(result, TwigJsonContext.Default.ConnectionMigrationPreview));
            else
            {
                var rows = new List<RenderNode>
                {
                    new RenderNode.Text($"  identity: {result.IdentityName ?? "(explicit setup required)"}"),
                    new RenderNode.Text($"  source: {result.SourceMirror ?? "(unavailable)"}"),
                    new RenderNode.Text($"  digest: {result.Digest}"),
                };
                rows.AddRange(result.Blockers.Select(x => (RenderNode)new RenderNode.Text("  blocker: " + x)));
                rows.AddRange(result.NextSteps.Select(x => (RenderNode)new RenderNode.Hint(x)));
                renderers.GetRenderer(output).Render(new RenderTree.RenderTree([
                    new RenderNode.Section($"Migration: {result.State} ({(result.CanApply ? "eligible" : "blocked")})", rows)]));
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
            try { TelemetryHelper.TrackCommand(telemetry, "connection migrate", output, exit, started); }
            catch (Exception) { Trace.TraceWarning("Optional command telemetry was unavailable."); }
        }
    }
}
