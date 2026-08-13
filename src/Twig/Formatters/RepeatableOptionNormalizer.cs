using System.Text.Json;

namespace Twig.Formatters;

/// <summary>
/// Entrypoint rewrite that makes genuinely repeatable options behave as documented
/// (AB#350).
/// </summary>
/// <remarks>
/// <para>
/// ConsoleAppFramework v5 does NOT support a repeated option. For a
/// <c>string[]</c> parameter its generated parser emits one <c>case "--field":</c>
/// arm that <em>assigns</em> (not appends) the bound array, so a second
/// <c>--field</c> silently overwrites the first and the command exits 0 having
/// written only the last one. That is the defect reported on AB#350: a write path
/// that succeeds having done less than it said, the same failure class as the
/// pre-0.86.1 <c>link predecessor</c> gap.
/// </para>
/// <para>
/// The framework's one supported multi-value form is a single value: either
/// comma-delimited or a JSON array. So rather than fight the generator, this
/// collapses every occurrence of a repeatable option into ONE occurrence carrying
/// a JSON array. JSON — not comma-joining — because field values legitimately
/// contain commas (<c>--field "Custom.Notes=a, b"</c>), and comma-joining would
/// silently split one assignment into two malformed ones. Trading a silent drop
/// for a silent split is not a fix.
/// </para>
/// <para>
/// This runs in <c>Program.cs</c> before <c>app.Run(args)</c>, alongside
/// <see cref="OutputFormatArgumentValidator"/>, and is deliberately conservative:
/// it rewrites only when an option is present more than once, stops at <c>--</c>,
/// and never touches an unrelated token. A single occurrence passes through
/// byte-identical, so the existing comma and JSON forms keep working.
/// </para>
/// </remarks>
public static class RepeatableOptionNormalizer
{
    /// <summary>
    /// Options documented as "Repeatable" that take a <c>string[]</c> parameter.
    /// Adding a repeatable option to <c>Program.cs</c> means adding it here too —
    /// <c>RepeatableOptionCoverageTests</c> fails the build otherwise.
    /// </summary>
    public static readonly IReadOnlyList<string> RepeatableOptions = ["--field", "--set"];

    /// <summary>
    /// Rewrites repeated occurrences of each known repeatable option into a single
    /// occurrence whose value is a JSON array of every supplied value, in order.
    /// Returns <paramref name="args"/> unchanged when nothing repeats.
    /// </summary>
    public static string[] Normalize(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new List<string>(args.Count);
        var collected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Index in `result` of the placeholder value slot for each option's FIRST
        // occurrence. Rewriting in place preserves the option's original position,
        // so nothing that depends on argument order (e.g. trailing `params`
        // operands) is disturbed.
        var valueSlot = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                // Everything after `--` is an operand, not an option.
                for (; i < args.Count; i++)
                    result.Add(args[i]);
                break;
            }

            var (option, inlineValue, consumesNext) = Classify(arg, args, i);
            if (option is null)
            {
                result.Add(arg);
                continue;
            }

            var value = inlineValue ?? args[i + 1];
            if (consumesNext)
                i++;

            if (!collected.TryGetValue(option, out var values))
            {
                values = [];
                collected[option] = values;

                // First occurrence: emit the option and reserve its value slot.
                result.Add(option);
                valueSlot[option] = result.Count;
                result.Add(value);
            }

            values.Add(value);
        }

        var rewritten = false;
        foreach (var (option, values) in collected)
        {
            if (values.Count < 2)
                continue;

            result[valueSlot[option]] = JsonSerializer.Serialize(
                values.ToArray(),
                ArgumentBinderJsonContext.Default.StringArray);
            rewritten = true;
        }

        return rewritten ? [.. result] : [.. args];
    }

    // Recognises `--opt value`, `--opt=value`. Returns (null, null, false) for
    // anything that is not a known repeatable option, or for a trailing `--opt`
    // with no following token — that is left to the parser to report rather than
    // being silently swallowed here.
    private static (string? Option, string? InlineValue, bool ConsumesNext) Classify(
        string arg,
        IReadOnlyList<string> args,
        int index)
    {
        foreach (var option in RepeatableOptions)
        {
            if (string.Equals(arg, option, StringComparison.Ordinal))
            {
                return index + 1 < args.Count
                    ? (option, null, true)
                    : (null, null, false);
            }

            var prefix = option + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return (option, arg[prefix.Length..], false);
        }

        return (null, null, false);
    }
}
