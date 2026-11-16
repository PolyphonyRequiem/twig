/// <summary>Refuses missing or unknown group verbs before command composition.</summary>
internal static class SubcommandGuard
{
    internal static readonly HashSet<string> PrefixesTakingPositional = ["process"];
    internal const int UsageExitCode = 1;

    internal static string? Validate(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-') || ProgressiveHelp.IsHelpRequest(args))
            return null;

        var chain = args[0];
        if (!CommandCatalog.KnownCommands.Contains(chain) && !IsGroupPrefix(chain))
            return null;

        var index = 1;
        while (index < args.Length && !args[index].StartsWith('-'))
        {
            var candidate = $"{chain} {args[index]}";
            if (CommandCatalog.KnownCommands.Contains(candidate) || IsGroupPrefix(candidate))
            {
                chain = candidate;
                index++;
                continue;
            }
            if (IsGroupPrefix(chain) && !PrefixesTakingPositional.Contains(chain))
                return $"Unknown subcommand: '{args[index]}' is not a '{chain}' command."
                    + Environment.NewLine + Environment.NewLine + DescribeVerbs(chain);
            return null;
        }

        return IsGroupPrefix(chain) && !CommandCatalog.KnownCommands.Contains(chain)
            ? $"Missing subcommand: '{chain}' requires one."
                + Environment.NewLine + Environment.NewLine + DescribeVerbs(chain)
            : null;
    }

    internal static bool IsGroupPrefix(string chain) => CommandCatalog.KnownCommands.Any(known =>
        known.StartsWith(chain + " ", StringComparison.Ordinal));

    internal static string DescribeVerbs(string chain)
    {
        var verbs = CommandCatalog.KnownCommands
            .Where(known => known.StartsWith(chain + " ", StringComparison.Ordinal))
            .Select(known => known[(chain.Length + 1)..].Split(' ')[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(verb => verb, StringComparer.Ordinal);
        return $"Valid '{chain}' subcommands: {string.Join(", ", verbs)}";
    }
}
