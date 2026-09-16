using ConsoleAppFramework;

/// <summary>
/// Progressive presentation over ConsoleAppFramework's declaration-generated help.
/// No command/options catalog is maintained here, and no command service is resolved.
/// </summary>
internal static class ProgressiveHelp
{
    internal static void ShowRoot(TextWriter? output = null)
    {
        output ??= Console.Out;
        output.WriteLine($"twig {VersionHelper.GetVersion()}");
        output.WriteLine("""

Usage: twig [command] [-h|--help] [--help-all] [--skill] [--version]

Choose a task:
  Get started       init, auth --help, sync
  Read and find     show, show-batch, query, history
  Explore work      workspace --help, bench --help, nav --help
  Understand types  process --help
  Draft work        seed --help
  Review and apply  proposal --help, pending
  Change an item    state, batch, note, update, patch, link --help
  Configure Twig    config --help, upgrade
  Agent guidance    --skill, skills --help

Help is offline; it does not initialize a workspace or sign in.
Run 'twig <command> --help' for options, defaults, examples, effects and failures.
Run 'twig --help-all' for the complete command catalog.
""");
    }

    /// <summary>
    /// Called before startup hooks and SQLite initialization. The real generated
    /// app.Run callback must bypass workspace service configuration for help.
    /// </summary>
    internal static bool TryShow(string[] args, Action<string[]> generatedHelp)
    {
        if (args.Length == 1 && args[0] == "--help-all")
        {
            generatedHelp(["--help"]);
            Console.WriteLine("Run 'twig <command> --help' for detailed usage, examples, effects and failures.");
            return true;
        }

        if (!IsHelpRequest(args)) return false;
        var requested = args[0] == "help" ? args[1..] : args;
        var words = requested.Where(a => a is not "-h" and not "--help").ToArray();
        if (words.Length == 0)
        {
            ShowRoot();
            return true;
        }

        // CAF embeds this text at compile time from registered declarations. Parsing
        // its padded command rows retains aliases and descriptions without reflection
        // (important for NativeAOT), or a second hand-written command registry.
        var catalog = ReadCatalog(CaptureGeneratedHelp(generatedHelp, ["--help"]));
        var command = ResolveCommand(words, catalog);
        if (command is null)
        {
            ShowUnknown(string.Join(' ', words.TakeWhile(word => !word.StartsWith('-'))));
            Environment.ExitCode = 1;
            return true;
        }

        var children = catalog.Where(row => row.Names.Any(name =>
            name.StartsWith(command + " ", StringComparison.Ordinal))).ToArray();
        var leaf = catalog.FirstOrDefault(row => row.Names.Contains(command, StringComparer.Ordinal));
        var hiddenLeaf = leaf is null && children.Length == 0 && GroupedHelp.KnownCommands.Contains(command);

        // A group can also have a useful bare command: process, workspace, nav,
        // config, workspace area. Keep that declaration's full options before the
        // focused child list. Hidden bare seed is not the canonical group operation.
        if (leaf is not null || hiddenLeaf)
        {
            generatedHelp([.. command.Split(' '), "--help"]);
            var canonical = leaf?.Names[0] ?? command;
            CommandExamples.ShowIfPresent(canonical.Split(' '));
            CommandHelpReference.ShowIfPresent(canonical);
        }
        else
        {
            Console.WriteLine($"Usage: twig {command} <command> --help");
        }

        if (children.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Commands in '{command}':");
            foreach (var child in children)
            {
                // Select the spelling in this group (e.g. the legacy 'plan' group)
                // without leaking commands from the alias's other namespace.
                var name = child.Names.First(n => n.StartsWith(command + " ", StringComparison.Ordinal));
                Console.WriteLine($"  {name,-28} {child.Description}");
            }
            Console.WriteLine();
            Console.WriteLine($"Run 'twig {command} <command> --help' for detailed usage.");
        }
        Console.WriteLine("Run 'twig --help-all' for the complete command catalog.");
        return true;
    }

    /// <summary>
    /// Compact unknown-command message. Deliberately does NOT print a command
    /// catalog: `twig --help` is the task-oriented overview and `twig --help-all`
    /// carries every declaration. Duplicating either here would drift.
    /// Called both from <see cref="TryShow"/>'s unknown branch and from
    /// <c>Program.cs</c>'s pre-routing unknown guard.
    /// </summary>
    internal static void ShowUnknown(string command, TextWriter? stderr = null)
    {
        stderr ??= Console.Error;
        stderr.WriteLine($"Unknown command: '{command}'");
        stderr.WriteLine("Run 'twig --help' for the task-oriented overview, or 'twig --help-all' for the complete catalog.");
    }

    internal static bool IsHelpRequest(string[] args)
    {
        if (args.Length == 0) return false;
        if (args[0] == "help") return true;
        // -- terminates options: `twig note -- --help` is a literal note, not help.
        foreach (var arg in args)
        {
            if (arg == "--") return false;
            if (arg is "-h" or "--help") return true;
        }
        return false;
    }

    private static string CaptureGeneratedHelp(Action<string[]> generatedHelp, string[] args)
    {
        using var writer = new StringWriter();
        var previous = ConsoleApp.Log;
        try
        {
            ConsoleApp.Log = message => writer.WriteLine(message);
            generatedHelp(args);
            return writer.ToString();
        }
        finally { ConsoleApp.Log = previous; }
    }

    internal sealed record CatalogRow(string[] Names, string Description);

    internal static IReadOnlyList<CatalogRow> ReadCatalog(string generated)
    {
        var rows = new List<CatalogRow>();
        var inCommands = false;
        using var reader = new StringReader(generated);
        while (reader.ReadLine() is { } line)
        {
            if (line == "Commands:") { inCommands = true; continue; }
            if (!inCommands) continue;
            // CAF command identities start at column 3. Multiline summaries are
            // indented further; trimming first turns narrative into fake commands.
            if (line.Length <= 2 || !line.StartsWith("  ", StringComparison.Ordinal)) continue;
            if (char.IsWhiteSpace(line[2]))
            {
                if (rows.Count > 0 && !string.IsNullOrWhiteSpace(line))
                    rows[^1] = rows[^1] with { Description = rows[^1].Description + " " + line.Trim() };
                continue;
            }
            var text = line[2..].TrimEnd();
            var separator = text.IndexOf("  ", StringComparison.Ordinal);
            var names = separator < 0 ? text : text[..separator];
            var description = separator < 0 ? "" : text[separator..].Trim();
            rows.Add(new(names.Split(", ", StringSplitOptions.RemoveEmptyEntries), description));
        }
        return rows;
    }

    private static string? ResolveCommand(string[] words, IReadOnlyList<CatalogRow> catalog)
    {
        string? resolved = null;
        var chain = "";
        foreach (var word in words)
        {
            if (word.StartsWith('-')) break;
            chain = chain.Length == 0 ? word : chain + " " + word;
            if (catalog.Any(row => row.Names.Any(name => name == chain || name.StartsWith(chain + " ", StringComparison.Ordinal)))
                || GroupedHelp.KnownCommands.Contains(chain))
                resolved = chain;
            else
            {
                // A group-only prefix cannot consume a positional value. A typo
                // such as `proposal bogus --help` is not successful group help.
                if (resolved is not null
                    && !SubcommandGuard.PrefixesTakingPositional.Contains(resolved)
                    && catalog.Any(row => row.Names.Any(name => name.StartsWith(resolved + " ", StringComparison.Ordinal))))
                    return null;
                break;
            }
        }
        return resolved;
    }
}
