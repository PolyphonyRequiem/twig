using System.Diagnostics;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig tree</c>: delegates to <see cref="TreeRenderingService"/> for
/// building and rendering the work item hierarchy.
/// </summary>
public sealed class TreeCommand(
    CommandContext ctx,
    TreeRenderingService treeRenderingService)
{
    /// <summary>Display the work item hierarchy as a tree.</summary>
    public async Task<int> ExecuteAsync(int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, bool noRefresh = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var effectiveDepth = all ? int.MaxValue : depth;
        var exitCode = await treeRenderingService.RenderTreeAsync(id, outputFormat, effectiveDepth, noLive, noRefresh, ct);
        ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "tree",
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }, new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        });
        return exitCode;
    }
}
