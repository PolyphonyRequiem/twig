using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Emits only fixed, privacy-safe command measurements to an optional host hook.
/// </summary>
public static class TelemetryHelper
{
    public static void TrackCommand(
        ITelemetryClient? client,
        string command,
        string outputFormat,
        int exitCode,
        long startTimestamp)
    {
        if (client is null) return;
        if (!CommandCatalog.KnownCommands.Contains(command) && command != "workspace-sync") return;

        var properties = new Dictionary<string, string>
        {
            ["command"] = command,
            ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["output_format"] = OutputFormats.Normalize(outputFormat) ?? "invalid",
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unsupported"
        };

        var metrics = new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        };

        try { client.TrackEvent("CommandExecuted", properties, metrics); }
        catch (Exception) { Debug.WriteLine("Optional telemetry failed; command outcome was preserved."); }
    }
}
