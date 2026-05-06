namespace Twig.Domain.Diagnostics;

/// <summary>
/// Privacy-safe tag key constants for Activity spans. Only these keys are permitted in traces.
/// Mirrors the telemetry allowlist: no org, project, user, type, name, path, field, title,
/// area, iteration, or repo identifiers are ever emitted.
/// </summary>
public static class TraceTags
{
    // Command-level
    public const string Command = "twig.command";
    public const string OutputFormat = "twig.output_format";
    public const string ExitCode = "twig.exit_code";

    // Operation-level
    public const string Operation = "twig.operation";
    public const string ItemCount = "twig.item_count";
    public const string Result = "twig.result";

    // Error
    public const string ExceptionKind = "twig.exception_kind";

    // Network
    public const string StatusCodeClass = "twig.status_code_class";
    public const string RetryCount = "twig.retry_count";

    // Performance
    public const string CacheHit = "twig.cache_hit";

    /// <summary>
    /// The complete set of allowed tag keys. Used by privacy guard tests to ensure
    /// no instrumentation site emits tags outside this allowlist.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>
    {
        Command,
        OutputFormat,
        ExitCode,
        Operation,
        ItemCount,
        Result,
        ExceptionKind,
        StatusCodeClass,
        RetryCount,
        CacheHit
    };
}
