using System.Diagnostics;
using Twig.Domain.Diagnostics;
using Twig.Domain.Interfaces;

namespace Twig.Commands;

/// <summary>
/// Manages an Activity span for the duration of a CLI command execution.
/// Integrates with both the existing <see cref="TelemetryHelper"/> (App Insights events)
/// and the new Activity-based tracing system.
/// <para/>
/// Usage:
/// <code>
/// using var scope = new CommandActivityScope("show", outputFormat);
/// // ... command logic ...
/// scope.Complete(exitCode);
/// TelemetryHelper.TrackCommand(client, "show", outputFormat, exitCode, scope.StartTimestamp);
/// </code>
/// </summary>
public sealed class CommandActivityScope : IDisposable
{
    private readonly Activity? _activity;

    /// <summary>
    /// High-resolution timestamp captured at scope creation — replaces manual
    /// <c>Stopwatch.GetTimestamp()</c> calls in commands.
    /// </summary>
    public long StartTimestamp { get; }

    public CommandActivityScope(string commandName, string outputFormat)
    {
        StartTimestamp = Stopwatch.GetTimestamp();
        _activity = ActivityHelper.StartCommand(commandName, outputFormat);
    }

    /// <summary>
    /// Marks the command as completed with the given exit code.
    /// Sets activity status to Ok (exit 0) or Error (non-zero).
    /// </summary>
    public void Complete(int exitCode)
    {
        ActivityHelper.Complete(_activity, exitCode);
    }

    /// <summary>
    /// Marks the command as failed due to an exception.
    /// Records exception type but NOT message (privacy).
    /// </summary>
    public void Fail(Exception ex)
    {
        ActivityHelper.Fail(_activity, ex);
    }

    /// <summary>
    /// Attaches the current Activity.Id to a telemetry TrackCommand call for correlation.
    /// Returns extra properties dict if an activity is active, null otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, string>? GetCorrelationProperties()
    {
        if (_activity?.Id is null) return null;
        return new Dictionary<string, string> { ["operation_id"] = _activity.Id };
    }

    public void Dispose()
    {
        _activity?.Dispose();
    }
}
