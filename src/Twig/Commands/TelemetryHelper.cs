using System.Diagnostics;
using System.Runtime.InteropServices;
using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Static helper that encapsulates the Stopwatch + TrackEvent wrapping pattern
/// duplicated across 10+ commands. Automatically correlates telemetry events with
/// the current Activity span (if active) via an <c>operation_id</c> property.
/// </summary>
public static class TelemetryHelper
{
    public static void TrackCommand(
        ITelemetryClient? client,
        string command,
        string outputFormat,
        int exitCode,
        long startTimestamp,
        IReadOnlyDictionary<string, string>? extraProperties = null,
        IReadOnlyDictionary<string, double>? extraMetrics = null)
    {
        if (client is null) return;

        var properties = new Dictionary<string, string>
        {
            ["command"] = command,
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = RuntimeInformation.OSDescription
        };

        // Correlate with active Activity span for distributed tracing
        if (Activity.Current?.Id is not null)
        {
            properties["operation_id"] = Activity.Current.Id;
        }

        if (extraProperties is not null)
        {
            foreach (var kvp in extraProperties)
                properties[kvp.Key] = kvp.Value;
        }

        var metrics = new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        };

        if (extraMetrics is not null)
        {
            foreach (var kvp in extraMetrics)
                metrics[kvp.Key] = kvp.Value;
        }

        client.TrackEvent("CommandExecuted", properties, metrics);
    }
}
