using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>Pulls only the current Bench's selectors and permitted context relationships.</summary>
internal sealed class BenchSyncCommand(
    CommandContext ctx,
    CurrentBenchResolver currentBench,
    BenchSyncOperation sync,
    IWorkItemRepository repository,
    IAuthenticationProvider authenticationProvider,
    IConnectionBindingService bindings,
    TwigPaths paths,
    RendererFactory rendererFactory)
{
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat,
        string? expectBench = null, string? expectBinding = null, string? expectIdentity = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);
        try
        {
            using var admission = await ConnectionOperationAdmission.AcquireAsync(authenticationProvider, ct);
            if (admission is null)
                throw new InvalidOperationException("Native Bench sync requires an admitted connection; reconnect before syncing.");
            using var operation = repository.AcquireOperation();
            var binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
            BrowserOriginGuard.EnsureExpected(binding, expectBinding, expectIdentity);
            var bench = await currentBench.ResolveAsync(ct, expectBench);

            var counts = await sync.PullAsync(bench, ct);

            rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree([
                new RenderNode.Record("benchSync", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["kind"] = RenderCell.String("benchSync"),
                    ["benchId"] = RenderCell.String(bench.Id.ToString(CultureInfo.InvariantCulture)),
                    ["benchName"] = RenderCell.String(bench.Name),
                    ["itemCount"] = RenderCell.Integer(counts.ItemCount),
                    ["memberCount"] = RenderCell.Integer(counts.MemberCount),
                    ["relationshipCount"] = RenderCell.Integer(counts.RelationshipCount),
                    ["protectedCount"] = RenderCell.Integer(counts.ProtectedCount),
                    ["message"] = RenderCell.String($"Synced Bench '{bench.Name}': {counts.ItemCount} item(s), {counts.ProtectedCount} protected; pull only."),
                })]));
            ReportTelemetry(0, counts.ItemCount);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.StderrWriter.WriteLine(fmt.FormatError(ex.Message));
            ReportTelemetry(1, null);
            return 1;
        }

        void ReportTelemetry(int exitCode, int? itemCount)
        {
            if (ctx.TelemetryClient is null) return;
            var version = VersionHelper.GetVersion();
            var metadata = version.IndexOf('+');
            if (metadata >= 0) version = version[..metadata];
            var metrics = new Dictionary<string, double>
            { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
            if (itemCount.HasValue) metrics["item_count"] = itemCount.Value;
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "workspace-sync",
                ["output_format"] = outputFormat,
                ["exit_code"] = exitCode.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = version,
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, metrics);
        }
    }

}
