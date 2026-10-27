namespace Twig.Formatters;

/// <summary>
/// Prevents structured CLI mutations from implicitly targeting the active work item.
/// </summary>
public static class StructuredMutationArgumentValidator
{
    public const int UsageExitCode = 2;

    private static readonly HashSet<string> ActiveItemMutationCommands =
    [
        "state",
        "batch",
        "note",
        "update",
        "patch",
        "edit",
        "link parent",
        "link unparent",
        "link reparent",
        "link predecessor",
        "link successor",
        "link related",
        "link unrelate",
        "link unlink",
        "link artifact",
    ];

    private static readonly HashSet<string> ActiveContextChangeCommands =
    [
        "up",
        "down",
        "next",
        "prev",
        "nav",
        "nav up",
        "nav down",
        "nav next",
        "nav prev",
        "nav back",
        "nav fore",
        "seed publish",
    ];

    private static readonly HashSet<string> ValueOptions =
    [
        "-o",
        "--output",
        "--id",
        "--ids",
        "--text",
        "--title",
        "--type",
        "--parent",
        "--state",
        "--reason",
        "--field",
        "--format",
        "--json",
        "--url",
        "--name",
        "--link-type",
        "--linkType",
        "--iteration",
        "--assigned-to",
        "--area",
        "--description",
        "--repo",
        "--branch",
        "--query",
        "--filter",
        "--path",
        "--project",
        "--organization",
    ];

    /// <summary>
    /// Returns a usage error when a structured invocation would use or change active-item
    /// context instead of naming its target explicitly.
    /// </summary>
    public static string? Validate(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var outputFormat = GetOutputFormat(args);
        if (outputFormat is null || string.Equals(outputFormat, OutputFormats.Default, StringComparison.Ordinal))
            return null;

        var command = GetCommand(args);
        if (command is null)
            return null;

        if (command == "set")
            return "twig set cannot be used with structured output because it changes the active work item.";

        if (command == "new" && HasBooleanOption(args, "--set"))
            return "twig new --set cannot be used with structured output because it changes the active work item.";

        if (command == "nav history" && outputFormat is not ("json" or "minimal"))
            return "Structured twig nav history supports only -o json or -o minimal; other formats may open an interactive selection and change active context.";

        if (ActiveContextChangeCommands.Contains(command))
            return $"twig {command} cannot be used with structured output because it reads or changes active work-item context.";

        if (command == "seed new"
            && !HasPositiveIntegerOption(args, "--parent")
            && !HasBooleanOption(args, "--no-parent"))
            return "Structured seed creation requires --parent <id> or --no-parent; it will not infer a parent from the active work item.";

        if (command == "seed chain" && !HasPositiveIntegerOption(args, "--parent"))
            return "Structured seed chains require --parent <id>; they will not infer a parent from the active work item.";

        if (command == "save"
            && !HasBooleanOption(args, "--all")
            && !HasPositiveIntegerOption(args, "--id")
            && !HasNumericPositional(args, 1))
            return "Structured save requires an explicit work-item ID or --all; it will not use the active work item.";

        if (!ActiveItemMutationCommands.Contains(command))
            return null;

        if (command == "batch" && HasNonEmptyValueOption(args, "--ids"))
            return null;

        if (command == "edit")
            return "twig edit is interactive and cannot use structured output; use 'twig patch --id <id>' instead.";

        if (HasPositiveIntegerOption(args, "--id"))
            return null;

        return $"{command} with structured output requires an explicit --id; it will not use the active work item.";
    }

    private static string? GetOutputFormat(IReadOnlyList<string> args)
    {
        var outputFormat = OutputFormats.Default;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;

            if (arg.StartsWith("--output=", StringComparison.Ordinal))
                outputFormat = arg["--output=".Length..];
            else if (arg.StartsWith("-o=", StringComparison.Ordinal))
                outputFormat = arg["-o=".Length..];
            else if ((arg == "--output" || arg == "-o") && i + 1 < args.Count)
                outputFormat = args[++i];
            else if (ShouldSkipOptionValue(args, i))
                i++;
        }

        return OutputFormats.Normalize(outputFormat);
    }

    private static string? GetCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return null;

        if (args[0] == "link" && args.Count > 1)
            return $"link {args[1]}";

        if (args[0] == "seed" && args.Count > 1)
            return $"seed {args[1]}";

        if (args[0] == "nav" && args.Count > 1)
            return $"nav {args[1]}";

        return args[0];
    }

    private static bool HasBooleanOption(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;

            if (arg == option)
                return true;

            if (arg.StartsWith(option + "=", StringComparison.Ordinal)
                && bool.TryParse(arg[(option.Length + 1)..], out var value)
                && value)
                return true;

            if (ShouldSkipOptionValue(args, i))
                i++;
        }

        return false;
    }

    private static bool HasPositiveIntegerOption(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;

            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
                return int.TryParse(arg[(option.Length + 1)..], out var attachedId) && attachedId > 0;

            if (arg == option)
            {
                if (i + 1 < args.Count && int.TryParse(args[i + 1], out var id) && id > 0)
                    return true;
                continue;
            }

            if (ShouldSkipOptionValue(args, i))
                i++;
        }

        return false;
    }

    private static bool HasNonEmptyValueOption(IReadOnlyList<string> args, string option)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;

            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
                return !string.IsNullOrWhiteSpace(arg[(option.Length + 1)..]);

            if (arg == option)
            {
                if (i + 1 < args.Count
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return true;
                continue;
            }

            if (ShouldSkipOptionValue(args, i))
                i++;
        }

        return false;
    }

    private static bool ShouldSkipOptionValue(IReadOnlyList<string> args, int index) =>
        ValueOptions.Contains(args[index])
        && index + 1 < args.Count
        && args[index + 1] is not ("--" or "-o" or "--output");

    private static bool HasNumericPositional(IReadOnlyList<string> args, int index) =>
        index < args.Count && int.TryParse(args[index], out var id) && id > 0;
}
