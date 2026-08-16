/// <summary>
/// AB#398 — turns "Argument 'world' is not recognized." into a message that names the fix.
///
/// <para>
/// Three commands (<c>note</c>, <c>new</c>, <c>seed chain</c>) declared their trailing words
/// as <c>params string[]</c> AFTER the <see cref="System.Threading.CancellationToken"/>.
/// ConsoleAppFramework 5.7.13 emits no positional slot for that shape at all, so EVERY bare
/// word was rejected — not merely a second one. AB#398 replaced those with single
/// <c>[Argument]</c> slots, which is the one multi-word spelling this argument reader
/// supports: a quoted value.
/// </para>
///
/// <para>
/// That fixes the accepted spellings and leaves one honest refusal behind. A user who types
/// <c>twig note hello world</c> has passed two arguments to a command that takes one, and the
/// generated parser reports the SECOND token as unrecognized — technically true and useless,
/// because the remedy is quoting, which the message never mentions. This guard says so:
/// </para>
///
/// <code>
/// error: unexpected extra argument 'world'.
///        Did you mean: twig note "hello world"
/// </code>
///
/// <para>
/// 🔴 The suggestion is only honest BECAUSE the quoted form now works. Emitting this hint
/// against the pre-AB#398 parser would have sent the user to a second identical failure — a
/// false green wearing a helpful tone, which is the AB#352/AB#79 defect this repo keeps
/// re-learning. Do not port this guard to a command whose quoted spelling is not accepted.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The decision is STRUCTURAL, not a per-verb list: a command is a candidate when its known
/// positional arity is exceeded by the bare words actually supplied. <see cref="Arity"/> is
/// hand-maintained and therefore pinned by reflection guards in
/// <c>StrayPositionalGuardTests</c>, which fail the build when a command gains or loses an
/// <c>[Argument]</c> parameter without the registry being updated — the same treatment
/// <see cref="SubcommandGuard"/> gives its two registries, and for the same reason: a
/// hand-maintained list is how the whole <c>bench</c> group shipped unreachable.
/// </para>
/// </remarks>
internal static class StrayPositionalGuard
{
    /// <summary>Exit code for a usage error, matching every other pre-routing guard.</summary>
    internal const int UsageExitCode = 1;

    /// <summary>
    /// Command chain → how many positional arguments it accepts, for commands whose surplus
    /// bare words are plausibly ONE quoted value.
    ///
    /// <para>
    /// 🔴 <c>init</c> is deliberately ABSENT despite taking two positionals. Its arguments are
    /// two unrelated identifiers (org, project), so surplus words are not a phrase that lost
    /// its quotes and "did you mean twig init myorg \"myproject extra\"" would be a confidently
    /// wrong remedy — the false-RED half of this repo's recurring defect. Only commands whose
    /// LAST positional is free text belong here.
    /// </para>
    /// <para>
    /// 🔴 <c>show-batch</c> is deliberately ABSENT for the same reason, established by
    /// measurement on AB#501 rather than by reading. Its positional is a COMMA-separated id
    /// list, so surplus bare words are not a phrase that lost its quotes: the hint this
    /// registry would emit, <c>twig show-batch "154 140"</c>, parses and exits 0 having
    /// returned NOTHING, because the splitter discards <c>"154 140"</c> as one non-numeric
    /// segment. That is a hint pointing at a silent false green — precisely what the summary
    /// above forbids. Only commands whose LAST positional is free text belong here.
    /// </para>
    /// </summary>
    internal static readonly Dictionary<string, int> Arity = new(StringComparer.Ordinal)
    {
        ["note"] = 1,
        ["edit"] = 1,
        ["seed chain"] = 1,
        ["new"] = 2,
    };

    /// <summary>
    /// Returns a diagnostic when <paramref name="args"/> passes more bare words than the
    /// command accepts; <c>null</c> when the arguments should be routed normally.
    /// </summary>
    internal static string? Validate(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            return null;

        // AB#352's lesson: a help request SUCCEEDED, and a usage error for a successful
        // request is a false RED. `twig note --help` must keep printing help and exiting 0.
        if (args.Any(a => a is "-h" or "--help"))
            return null;

        var chain = LongestKnownChain(args, out var index);
        if (chain is null || !Arity.TryGetValue(chain, out var allowed))
            return null;

        var positionals = new List<string>();
        while (index < args.Length)
        {
            var token = args[index];
            if (token.StartsWith('-'))
            {
                // Skip the option and, when it is not `--flag=value` and the next token is not
                // itself an option, the value it consumes. Over-skipping here can only SUPPRESS
                // the hint, never fabricate one, which is the safe direction for a guard whose
                // false positive would be a false RED on a working command line.
                if (!token.Contains('=') && index + 1 < args.Length && !args[index + 1].StartsWith('-'))
                    index += 2;
                else
                    index++;
                continue;
            }

            positionals.Add(token);
            index++;
        }

        if (positionals.Count <= allowed)
            return null;

        var stray = positionals[allowed];
        var quoted = string.Join(" ", positionals.Skip(allowed == 0 ? 0 : allowed - 1));
        var prefix = allowed <= 1
            ? chain
            : $"{chain} {string.Join(" ", positionals.Take(allowed - 1))}";

        return $"error: unexpected extra argument '{stray}'."
            + Environment.NewLine
            + $"       Did you mean: twig {prefix} \"{quoted}\"";
    }

    /// <summary>
    /// Walks the longest prefix of <paramref name="args"/> that is a known command, so
    /// <c>seed chain</c> is recognised as one chain rather than <c>seed</c> plus a stray word.
    /// </summary>
    private static string? LongestKnownChain(string[] args, out int index)
    {
        index = 1;
        var chain = args[0];
        if (!GroupedHelp.KnownCommands.Contains(chain) && !SubcommandGuard.IsGroupPrefix(chain))
            return null;

        while (index < args.Length && !args[index].StartsWith('-'))
        {
            var candidate = $"{chain} {args[index]}";
            if (!GroupedHelp.KnownCommands.Contains(candidate) && !SubcommandGuard.IsGroupPrefix(candidate))
                break;
            chain = candidate;
            index++;
        }

        return chain;
    }
}
