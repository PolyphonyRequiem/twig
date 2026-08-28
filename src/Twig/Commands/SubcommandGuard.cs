/// <summary>
/// AB#79 — rejects an unrecognized SUBCOMMAND before ConsoleAppFramework can absorb it.
///
/// <para>
/// The pre-routing interception in <c>Program.cs</c> only ever asked
/// <see cref="GroupedHelp.IsKnownCommand"/>, which inspects <c>args[0]</c>. Every compound
/// verb therefore sailed past it the moment its GROUP prefix was known, and what happened
/// next depended on whether the group had a bare handler — none of the outcomes were an
/// error, and all of them exited 0:
/// </para>
/// <list type="bullet">
///   <item><description>
///     No bare handler (<c>link</c>, <c>bench</c>, <c>auth</c>, <c>ohmyposh</c>,
///     <c>workspace sprint</c>) — ConsoleAppFramework printed top-level usage and exited 0.
///     This is the no-op the ticket describes.
///   </description></item>
///   <item><description>
///     A bare handler taking no positional (<c>nav</c>, <c>workspace</c>, <c>area</c>) — the
///     stray word was ignored and the BARE command ran. <c>twig nav bogus</c> launched the
///     interactive navigator.
///   </description></item>
///   <item><description>
///     A bare handler taking a positional (<c>seed</c>) — the stray word was consumed as that
///     positional. <c>twig seed bogus</c> did not print usage at all: it CREATED a seed titled
///     "bogus". Worse than a no-op, because a false green covers a real side effect.
///   </description></item>
/// </list>
///
/// <para>
/// The guard resolves the longest known command chain, then decides on structure rather than
/// on a per-verb list: the next word is an unknown subcommand whenever the chain so far is a
/// group prefix whose bare form does not legitimately consume a positional value.
/// </para>
///
/// <para>
/// Commands taking positional arguments are unaffected by construction. <c>set</c>,
/// <c>show</c>, <c>state</c> and <c>note</c> are not group prefixes — no entry in
/// <see cref="GroupedHelp.KnownCommands"/> begins <c>"set "</c> — so <c>twig set 123</c> never
/// reaches the rejection branch, and
/// <c>GroupedHelpTests.IsKnownCommand_FallsBackToTopLevelWhenCompoundUnknown</c> keeps
/// passing unmodified. <c>process</c> and <c>config</c> ARE group prefixes whose bare form
/// takes a value (<c>twig process Bug</c>, <c>twig config org</c>), so they are listed in
/// <see cref="PrefixesTakingPositional"/> and keep working too.
/// </para>
/// </summary>
/// <remarks>
/// Both registries below are hand-maintained, and a hand-maintained list is exactly how the
/// bench group became unreachable (see the comment on <c>"bench"</c> in
/// <see cref="GroupedHelp.KnownCommands"/>). They are therefore pinned by reflection guards in
/// <c>SubcommandGuardTests</c> that fail the build when a bare handler is added, removed, or
/// gains an <c>[Argument]</c> without the registry being updated.
/// </remarks>
internal static class SubcommandGuard
{
    /// <summary>
    /// Group prefixes whose bare form legitimately consumes a positional VALUE, so a following
    /// word is data rather than a subcommand: <c>twig process Bug</c>, <c>twig config org</c>.
    /// </summary>
    internal static readonly HashSet<string> PrefixesTakingPositional =
    [
        "process",
        "config",
    ];

    /// <summary>
    /// Group prefixes with no bare handler at all. Invoking one with no subcommand is itself an
    /// error — previously it printed top-level usage and exited 0.
    /// </summary>
    internal static readonly HashSet<string> PrefixesWithoutBareHandler =
    [
        "link",
        "bench",
        "auth",
        "ohmyposh",
        "plan",
        "proposal",
        "workspace sprint",
    ];

    /// <summary>
    /// Group prefixes whose bare form takes a positional that the guard nonetheless treats as a
    /// SUBCOMMAND. Exactly one entry, and it needs its reason recorded.
    ///
    /// <para>
    /// <c>seed</c>'s bare handler is a <c>[Hidden]</c> backward-compat alias of <c>seed new</c>
    /// taking a title. Deferring to it is how <c>twig seed bogus</c> silently CREATED a seed
    /// titled "bogus" instead of reporting a typo — the most damaging form of this defect,
    /// because a false green covered a real write. Titles are reachable via the canonical
    /// <c>twig seed new "bogus"</c>, so the alias loses the ambiguity.
    /// </para>
    /// </summary>
    internal static readonly HashSet<string> PrefixesWhereSubcommandWins =
    [
        "seed",
    ];

    /// <summary>Exit code for a usage error, matching the unknown-top-level-command path.</summary>
    internal const int UsageExitCode = 1;

    /// <summary>
    /// Returns a diagnostic when <paramref name="args"/> names an unrecognized subcommand or
    /// omits a required one; <c>null</c> when the arguments should be routed normally.
    /// </summary>
    internal static string? Validate(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
            return null;

        // AB#352's lesson, applied here: a help request SUCCEEDED, and a usage error for a
        // successful request is a false RED, which corrodes the exit code as surely as a false
        // green. `twig link --help` must keep printing help and exiting 0.
        if (args.Any(a => a is "-h" or "--help"))
            return null;

        // Walk the longest chain of words that is still a known command.
        var chain = args[0];
        if (!IsKnownOrPrefix(chain))
            return null; // unknown TOP-LEVEL command — already handled by GroupedHelp.ShowUnknown.

        var index = 1;
        while (index < args.Length && !args[index].StartsWith('-'))
        {
            var candidate = $"{chain} {args[index]}";
            if (GroupedHelp.KnownCommands.Contains(candidate) || IsGroupPrefix(candidate))
            {
                chain = candidate;
                index++;
                continue;
            }

            // The chain cannot be extended. Whether the word is a typo'd subcommand or a
            // legitimate positional value depends only on the chain, never on the word.
            if (IsGroupPrefix(chain)
                && (PrefixesWhereSubcommandWins.Contains(chain)
                    || !PrefixesTakingPositional.Contains(chain)))

            {
                return $"Unknown subcommand: '{args[index]}' is not a '{chain}' command."
                    + Environment.NewLine
                    + Environment.NewLine
                    + DescribeVerbs(chain);
            }

            return null; // positional argument — route normally.
        }

        // Ran out of words. A prefix-only group invoked bare did nothing and exited 0.
        if (PrefixesWithoutBareHandler.Contains(chain))
        {
            return $"Missing subcommand: '{chain}' requires one."
                + Environment.NewLine
                + Environment.NewLine
                + DescribeVerbs(chain);
        }

        return null;
    }

    private static bool IsKnownOrPrefix(string word)
        => GroupedHelp.KnownCommands.Contains(word) || IsGroupPrefix(word);

    /// <summary>True when at least one known command extends <paramref name="chain"/> by a word.</summary>
    internal static bool IsGroupPrefix(string chain)
        => GroupedHelp.KnownCommands.Any(known =>
            known.Length > chain.Length
            && known.StartsWith(chain, StringComparison.Ordinal)
            && known[chain.Length] == ' ');

    /// <summary>Lists the verbs that DO extend <paramref name="chain"/>, so the error is actionable.</summary>
    internal static string DescribeVerbs(string chain)
    {
        var verbs = GroupedHelp.KnownCommands
            .Where(known =>
                known.Length > chain.Length
                && known.StartsWith(chain, StringComparison.Ordinal)
                && known[chain.Length] == ' ')
            .Select(known => known[(chain.Length + 1)..])
            // Only the immediate next word: 'workspace area add' is reachable via 'workspace area'.
            .Where(rest => !rest.Contains(' '))
            .OrderBy(rest => rest, StringComparer.Ordinal)
            .ToList();

        return $"Valid '{chain}' subcommands: {string.Join(", ", verbs)}";
    }
}
