using System.Diagnostics;

namespace Twig.Domain.Diagnostics;

/// <summary>
/// Helper for starting and completing command-level Activity spans with guaranteed
/// status and tag assignment, even when exceptions occur.
/// </summary>
public static class ActivityHelper
{
    /// <summary>
    /// Starts a command-level activity. Returns null if no listener is active.
    /// Caller should use <see cref="Complete"/> or <see cref="Fail"/> in a try/finally.
    /// </summary>
    public static Activity? StartCommand(string commandName, string outputFormat)
    {
        var activity = TwigActivitySource.Source.StartActivity(
            $"command.{commandName}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag(TraceTags.Command, commandName);
            activity.SetTag(TraceTags.OutputFormat, outputFormat);
        }

        return activity;
    }

    /// <summary>
    /// Starts an ADO client operation activity.
    /// </summary>
    public static Activity? StartAdoOperation(string operation)
    {
        var activity = TwigActivitySource.Source.StartActivity(
            $"ado.{operation}",
            ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag(TraceTags.Operation, operation);
        }

        return activity;
    }

    /// <summary>
    /// Starts a SQLite internal operation activity.
    /// </summary>
    public static Activity? StartSqliteOperation(string operation)
    {
        var activity = TwigActivitySource.Source.StartActivity(
            $"sqlite.{operation}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag(TraceTags.Operation, operation);
        }

        return activity;
    }

    /// <summary>
    /// Starts a rendering internal operation activity.
    /// </summary>
    public static Activity? StartRenderOperation(string operation)
    {
        var activity = TwigActivitySource.Source.StartActivity(
            $"render.{operation}",
            ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag(TraceTags.Operation, operation);
        }

        return activity;
    }

    /// <summary>
    /// Marks an activity as successfully completed with exit code.
    /// </summary>
    public static void Complete(Activity? activity, int exitCode)
    {
        if (activity is null) return;

        activity.SetTag(TraceTags.ExitCode, exitCode);
        if (exitCode != 0)
        {
            activity.SetStatus(ActivityStatusCode.Error);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    /// <summary>
    /// Marks an activity as failed due to an exception. Does NOT record exception
    /// message or stack trace (privacy: messages may contain ADO content).
    /// </summary>
    public static void Fail(Activity? activity, Exception ex)
    {
        if (activity is null) return;

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(TraceTags.ExceptionKind, ex.GetType().Name);
    }

    /// <summary>
    /// Sets the item count tag on an activity (safe — only set when activity is active).
    /// </summary>
    public static void SetItemCount(Activity? activity, int count)
    {
        activity?.SetTag(TraceTags.ItemCount, count);
    }

    /// <summary>
    /// Sets the HTTP status code class (e.g. "2xx", "4xx") on an activity.
    /// </summary>
    public static void SetStatusCodeClass(Activity? activity, int httpStatusCode)
    {
        var codeClass = httpStatusCode switch
        {
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 => "5xx",
            _ => "unknown"
        };
        activity?.SetTag(TraceTags.StatusCodeClass, codeClass);
    }
}
