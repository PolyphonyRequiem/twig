namespace Twig.Formatters;

/// <summary>
/// Entrypoint validation for the <c>-o</c>/<c>--output</c> option (wayfinder 0019).
/// </summary>
/// <remarks>
/// Validated ONCE, in <c>Program.cs</c> before <c>app.Run(args)</c>, rather
/// than per-command. An unknown value exits non-zero with the accepted values
/// named in the message, instead of silently falling through a factory
/// catch-all to human output with exit 0.
/// </remarks>
public static class OutputFormatArgumentValidator
{
    /// <summary>Exit code used for a usage error, matching the command-level convention.</summary>
    public const int UsageExitCode = 2;

    /// <summary>
    /// Scans <paramref name="args"/> for <c>-o</c>/<c>--output</c> and validates
    /// the supplied value against <see cref="OutputFormats.Accepted"/>.
    /// Returns <see langword="null"/> when every occurrence is acceptable (or
    /// none is present); otherwise returns the error message to write to stderr.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: it only rejects a value it is certain is an
    /// <c>-o</c> value. A bare trailing <c>-o</c> with no following token is
    /// left to the argument parser to report, and <c>--</c> ends scanning so a
    /// pass-through operand is never mistaken for a format.
    /// </remarks>
    public static string? Validate(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--", StringComparison.Ordinal))
                break;

            string? value = null;

            // --output=json / -o=json
            if (arg.StartsWith("--output=", StringComparison.Ordinal))
                value = arg["--output=".Length..];
            else if (arg.StartsWith("-o=", StringComparison.Ordinal))
                value = arg["-o=".Length..];
            // --output json / -o json
            else if ((string.Equals(arg, "--output", StringComparison.Ordinal)
                      || string.Equals(arg, "-o", StringComparison.Ordinal))
                     && i + 1 < args.Count)
            {
                value = args[i + 1];
                i++;
            }

            if (value is null)
                continue;

            if (!OutputFormats.IsAccepted(value))
                return Message(value);
        }

        return null;
    }

    /// <summary>The stderr message emitted for an unknown format value.</summary>
    public static string Message(string value) =>
        $"Unknown output format '{value}'. Valid formats: {OutputFormats.Describe()}.";
}
