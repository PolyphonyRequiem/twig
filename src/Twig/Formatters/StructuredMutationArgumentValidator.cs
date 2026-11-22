namespace Twig.Formatters;

/// <summary>
/// Checks explicit invocation scope before workspace discovery or server dispatch.
/// </summary>
public static class StructuredMutationArgumentValidator
{
    public const int UsageExitCode = 2;

    private static readonly HashSet<string> ItemMutationCommands =
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
        "--local-id",
        "--fields",
        "--sections",
        "--depth",
        "--file",
        "--child",
        "--bench",
        "--view",
        "--expect-bench",
        "--expect-revision",
        "--expect-contents",
        "--index",
        "--backlog",
        "--set",
        "--comment",
        "--confirm",
    ];

    /// <summary>
    /// Returns a usage error when the invocation omits its required target or uses
    /// an interactive-only operation with structured output.
    /// </summary>
    public static string? Validate(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--help") || args.Contains("-h")) return null;
        var machine = GetOutputFormat(args) is { } format && format != OutputFormats.Default;
        var command = GetCommand(args);
        if (command is null) return null;
        if (args[0] is "plan" && args.Count > 1 && args[1] is "validate" or "preview" or "apply" or "status"
            && !HasNonEmptyValueOption(args, "--file"))
            return "Specify an explicit plan file with --file.";
        if (command is "workspace" or "ws" && (args.Count == 1 || args[1].StartsWith('-'))
            && !HasNonEmptyValueOption(args, "--bench"))
            return "Name the Bench with --bench <name-or-guid>.";
        if (args[0] == "bench" && args.Count > 1)
        {
            var nameAt = args[1] switch
            {
                "rules" or "rename" or "delete" or "create" => 2,
                "rule" when args.Count > 2 && args[2] is "add" or "remove" => 3,
                _ => -1,
            };
            if (nameAt >= 0 && !Positionals(args, nameAt).Any())
                return "Name the Bench explicitly.";
        }
        if (command is "link local related" or "link local unrelate" or "link local list")
        {
            var positional = 0;
            var sources = new List<string>();
            var positionalOnly = false;
            for (var i = 3; i < args.Count; i++)
            {
                var arg = args[i];
                if (!positionalOnly && arg is "--help" or "-h") return null;
                if (!positionalOnly && arg == "--") { positionalOnly = true; continue; }
                if (!positionalOnly && arg.StartsWith("--id=", StringComparison.Ordinal)) sources.Add(arg[5..]);
                else if (!positionalOnly && arg == "--id" && i + 1 < args.Count) sources.Add(args[++i]);
                else if (!positionalOnly && ShouldSkipOptionValue(args, i)) i++;
                else if (positionalOnly || !arg.StartsWith('-') || int.TryParse(arg, out _)) positional++;
            }
            if (sources.Count > 1)
                return "Specify --id once; repeated source targets are refused.";
            if (positional > 1 || command != "link local list" && positional != 1)
                return "Use 'twig link local related <target> --id <source>' (or unrelate); the old two-GUID positional form is retired.";
            if (command != "link local list" && sources.Count != 1)
                return $"{command} requires an explicit --id source; no active-item fallback.";
            if (command == "link local list" && sources.Count == 0 && positional == 0)
                return "List requires an explicit item ID.";
        }
        if (command is "show" or "tree")
        {
            var readError = ValidateReadTarget(args, command);
            if (readError is not null) return readError;
        }

        if (command == "seed publish" && machine)
            return "Structured seed publication is refused. Use an explicit reviewed plan apply.";
        if (command is "seed publish" or "seed discard" or "seed edit"
            && !(command == "seed publish" && HasBooleanOption(args, "--all"))
            && !Positionals(args, 2).Any(value => int.TryParse(value, out var n) && n < 0))
            return "Specify an explicit negative seed ID.";
        if (command is "seed link" or "seed unlink" && Positionals(args, 2).Count() < 2)
            return "Specify both seed link endpoints.";
        if (command is "link parent" or "link reparent" or "link predecessor" or "link successor" or "link related" or "link unrelate"
            && !Positionals(args, 2).Any(value => int.TryParse(value, out var n) && n > 0))
            return "Specify the explicit relationship endpoint.";
        if (command == "link unlink" && Positionals(args, 2).Count() < 2)
            return "Specify the link type and endpoint to remove.";
        if (command == "link artifact" && !Positionals(args, 2).Any())
            return "Specify the resource URL to link.";

        if (command is "link local related" or "link local unrelate")
            return HasIntegerOption(args, "--id", static id => id != 0) || HasCanonicalLocalId(args)
                ? null : $"{command} requires an explicit --id (native number, negative seed alias or canonical lowercase GUIDv7).";

        if (command == "seed new"
            && !HasIntegerOption(args, "--parent", static id => id != 0)
            && !HasBooleanOption(args, "--no-parent"))
            return "Seed creation requires --parent <id> or --no-parent.";

        if (command == "seed chain" && !HasIntegerOption(args, "--parent", static id => id != 0))
            return "Seed chains require --parent <id>.";

        if (command is "save" or "discard"
            && !HasBooleanOption(args, "--all")
            && !HasIntegerOption(args, "--id", static id => id != 0)
            && !Positionals(args, 1).Any(value => int.TryParse(value, out var n) && n != 0))
            return $"twig {command} requires an explicit work-item ID or --all.";

        if (!ItemMutationCommands.Contains(command))
            return null;

        if (command == "batch" && HasNonEmptyValueOption(args, "--ids"))
            return null;

        if (command == "edit" && machine)
            return "twig edit is interactive and cannot use structured output; use 'twig patch --id <id>' instead.";

        if (!machine && command == "patch" && Positionals(args, 1).Any(value => int.TryParse(value, out var n) && n > 0))
            return null;
        if (!machine && command is "link parent" or "link reparent" or "link unparent"
            && (HasIntegerOption(args, "--child", static id => id > 0)
                || Positionals(args, 2).Skip(command == "link unparent" ? 0 : 1).Any(value => int.TryParse(value, out var n) && n > 0)))
            return null;
        // Negative IDs are explicit local seed aliases, not active-item fallbacks.
        if (HasIntegerOption(args, "--id", static id => id != 0))
            return null;

        return $"{command} requires an explicit --id.";
    }

    internal static string? GetOutputFormat(IReadOnlyList<string> args)
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

    private static string? ValidateReadTarget(IReadOnlyList<string> args, string command)
    {
        int? target = null;
        var positionalOnly = false;
        var positionalSeen = false;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!positionalOnly && arg == "--")
            {
                positionalOnly = true;
                continue;
            }

            var named = !positionalOnly && (arg == "--id" || arg.StartsWith("--id=", StringComparison.Ordinal));
            var value = arg;
            if (named)
            {
                value = arg == "--id" && i + 1 < args.Count ? args[++i]
                    : arg.StartsWith("--id=", StringComparison.Ordinal) ? arg["--id=".Length..] : "";
            }
            else if (!positionalOnly && ShouldSkipOptionValue(args, i))
            {
                i++;
                continue;
            }

            if (!named)
            {
                if (!positionalOnly && arg.StartsWith('-') && !int.TryParse(arg, out _))
                    continue;
                if (positionalSeen) continue;
                positionalSeen = true;
            }

            if (int.TryParse(value, out var id) && id != 0)
            {
                if (target.HasValue && target.Value != id)
                    return "Conflicting work-item targets: positional ID and --id must identify the same item.";
                target = id;
            }
            else if (named)
                return $"twig {command} --id requires a nonzero integer work-item ID.";
        }

        var local = command == "show" && HasNonEmptyValueOption(args, "--local-id");
        if (local && target.HasValue)
            return "Conflicting work-item targets: use an integer ID or --local-id, not both.";
        if (target.HasValue || local || command == "tree" && HasBooleanOption(args, "--all"))
            return null;
        return $"twig {command} requires an explicit work-item ID (positional or --id).";
    }

    private static string? GetCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return null;

        if (args.Count > 2 && args[0] == "link" && args[1] == "local")
            return $"link local {args[2]}";
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

    private static bool HasCanonicalLocalId(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--") break;
            var value = args[i].StartsWith("--id=", StringComparison.Ordinal) ? args[i][5..]
                : args[i] == "--id" && i + 1 < args.Count ? args[++i] : null;
            if (value is not null)
                return value.Length == 36 && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b'
                    && Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;
            if (ShouldSkipOptionValue(args, i)) i++;
        }
        return false;
    }
    private static bool HasIntegerOption(IReadOnlyList<string> args, string option, Func<int, bool> accept)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                return false;

            if (arg.StartsWith(option + "=", StringComparison.Ordinal))
                return int.TryParse(arg[(option.Length + 1)..], out var attachedId) && accept(attachedId);

            if (arg == option)
            {
                if (i + 1 < args.Count && int.TryParse(args[i + 1], out var id) && accept(id))
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

    private static IEnumerable<string> Positionals(IReadOnlyList<string> args, int start)
    {
        var positionalOnly = false;
        for (var i = start; i < args.Count; i++)
        {
            if (!positionalOnly && args[i] == "--") positionalOnly = true;
            else if (!positionalOnly && ShouldSkipOptionValue(args, i)) i++;
            else if (positionalOnly || !args[i].StartsWith('-') || int.TryParse(args[i], out _))
                yield return args[i];
        }
    }
}
