using System.Reflection;
using System.Text.Json;
using Twig;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

/// <summary>
/// AB#389 — pins every <c>--help</c> example against the parameters its command actually
/// declares, so the two cannot drift apart again.
/// </summary>
public sealed class CommandExampleParameterDriftTests
{
    /// <summary>
    /// Every positional token in an example must be backed by an <c>[Argument]</c>
    /// parameter, and every <c>--flag</c> by a declared option.
    /// </summary>
    [Fact]
    public void EveryExample_IsAcceptedByItsCommandSignature()
    {
        var commands = DiscoverCommands();
        commands.ShouldNotBeEmpty("no public command methods discovered — the reflection probe is broken, "
            + "and a probe that finds nothing passes vacuously");

        var examplesTable = LoadExamplesTable(commands);
        examplesTable.ShouldNotBeEmpty("no examples discovered — see above; an empty table cannot fail");
        examplesTable.ShouldContainKey("show", "the current public read command must contribute real examples");

        var failures = new List<string>();

        foreach (var (commandName, examples) in examplesTable)
        {
            if (!commands.TryGetValue(commandName, out var signature))
                signature = null; // A group heading need not be a [Command] itself.

            foreach (var example in examples)
            {
                var invocation = example.Trim();
                if (invocation is "twig --help" or "twig --version")
                    continue; // ConsoleAppFramework owns these root options.

                // 🔴 A GROUP's entry legitimately advertises its SUBCOMMANDS: the "nav" key
                // lists `twig nav up` and `twig nav down`, which are separate [Command]s with
                // their own signatures. Validate each example against the most specific
                // command it actually names, or a group heading reads as its own bare verb
                // taking extra positionals — 3 of the first run's false failures.
                var effectiveName = MostSpecificCommand(invocation, commandName, commands);
                if (!commands.TryGetValue(effectiveName, out signature)
                    || !invocation.StartsWith("twig " + effectiveName + " ", StringComparison.Ordinal)
                        && invocation != "twig " + effectiveName)
                {
                    failures.Add($"{commandName}: example names no current public command: {invocation}");
                    continue;
                }

                var tokens = Tokenize(invocation, effectiveName);

                var positionals = CountPositionals(tokens, signature);
                if (positionals > signature.PositionalCount)
                {
                    failures.Add(
                        $"{commandName}: example '{invocation}' passes {positionals} positional argument(s) "
                        + $"but the command declares {signature.PositionalCount} [Argument] parameter(s)");
                }

                foreach (var flag in ExtractFlags(tokens))
                {
                    if (!signature.Options.Contains(flag))
                    {
                        failures.Add(
                            $"{commandName}: example '{invocation}' passes '--{flag}' "
                            + $"but the command declares no such option "
                            + $"(declared: {(signature.Options.Count == 0 ? "none" : string.Join(", ", signature.Options.Select(o => "--" + o)))})");
                    }
                }
            }
        }

        failures.ShouldBeEmpty(
            "--help examples must match the parameters their commands declare. "
            + "An example the command rejects is worse than no example: it is a promise the tool breaks, "
            + "and a caller who follows it gets a hard error with no working alternative shown.\n"
            + string.Join("\n", failures));
    }

    /// <summary>
    /// Deterministic controls for the shell-operator cut and boolean arity. Each case pins a
    /// way the cut could turn a real drift into a false green.
    /// </summary>
    [Theory]
    // Valid shell redirection: the operator and its target never reach argv.
    [InlineData("twig probe --ids 42 --proposal-only > repair.json", 0)]
    // A boolean takes no value, so a stray word before the redirection is a real positional.
    [InlineData("twig probe --ids 42 --proposal-only bogus > repair.json", 1)]
    // A QUOTED operator is a literal argument value, not a shell operator; the cut must not
    // hide the genuine extra positional that follows it.
    [InlineData("twig probe --ids \">\" extra", 1)]
    [InlineData("twig probe \">\"", 1)]
    [InlineData("twig probe --proposal-only \"|\" extra", 2)]
    [InlineData("twig probe --ids 'two words' extra", 1)]
    [InlineData("twig probe --ids '>' extra # comment", 1)]
    public void ShellOperatorCut_AndBooleanArity_CountOnlyRealPositionals(string invocation, int expected)
    {
        var signature = new CommandSignature(
            PositionalCount: 0,
            Options: new HashSet<string>(StringComparer.Ordinal) { "ids", "proposal-only" },
            BooleanOptions: new HashSet<string>(StringComparer.Ordinal) { "proposal-only" });

        CountPositionals(Tokenize(invocation, "probe"), signature).ShouldBe(expected);
    }

    /// <summary>
    /// Use a current public command; internal refusal stubs are not public discovery controls.
    /// </summary>
    [Fact]
    public void BooleanArity_IsReflectedFromTheRealCommandSignature()
    {
        var commands = DiscoverCommands();
        commands.ShouldContainKey("show");
        commands.ShouldNotContainKey("policy reconcile");
        var show = commands["show"];
        show.BooleanOptions.ShouldContain("refresh");
        show.Options.ShouldContain("id");
        show.BooleanOptions.ShouldNotContain("id");
    }

    private sealed record CommandSignature(
        int PositionalCount,
        IReadOnlySet<string> Options,
        IReadOnlySet<string> BooleanOptions);

    /// <summary>
    /// Longest declared command name that the invocation begins with, so an example under a
    /// group key is judged against the subcommand it actually invokes.
    /// </summary>
    private static string MostSpecificCommand(
        string invocation,
        string fallback,
        Dictionary<string, CommandSignature> commands)
    {
        var words = invocation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words[0] != "twig")
            return fallback;

        for (var take = words.Length - 1; take >= 1; take--)
        {
            var candidate = string.Join(' ', words.Skip(1).Take(take));
            if (commands.ContainsKey(candidate))
                return candidate;
        }

        return fallback;
    }

    private static Dictionary<string, CommandSignature> DiscoverCommands()
    {
        var result = new Dictionary<string, CommandSignature>(StringComparer.Ordinal);

        // 🔴 Match attributes BY NAME, not by type. ConsoleAppFramework source-generates
        // CommandAttribute/ArgumentAttribute into every assembly that references it, so the
        // copy in this test assembly is a DIFFERENT type from the copy in twig — a
        // GetCustomAttribute<CommandAttribute>() probe compiles (with CS0436) and then
        // silently matches nothing, which passes vacuously. The emptiness guards in the
        // test body exist to catch exactly that.
        foreach (var method in typeof(TwigConsoleApp).GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var command = GetAttribute(method, "CommandAttribute");
            var commandName = command?.GetType().GetProperty("Command")?.GetValue(command) as string
                ?? ToKebabCase(method.Name);
            if (string.IsNullOrWhiteSpace(commandName))
                continue;

            var positional = 0;
            var options = new HashSet<string>(StringComparer.Ordinal) { "help", "version" };
            var booleans = new HashSet<string>(StringComparer.Ordinal) { "help", "version" };

            foreach (var p in method.GetParameters())
            {
                if (p.ParameterType == typeof(CancellationToken))
                    continue;

                if (HasAttribute(p, "ArgumentAttribute"))
                {
                    positional++;
                    continue;
                }

                // ConsoleAppFramework exposes non-[Argument] parameters as --kebab-case options.
                var option = ToKebabCase(p.Name!);
                options.Add(option);
                // ConsoleAppFramework binds a bool option as a switch that consumes no value.
                if (p.ParameterType == typeof(bool))
                    booleans.Add(option);
            }

            result.TryAdd(commandName, new CommandSignature(positional, options, booleans))
                .ShouldBeTrue($"duplicate public command declaration: {commandName}");
        }

        return result;
    }

    private static bool HasAttribute(ParameterInfo p, string name) =>
        p.GetCustomAttributes().Any(a => a.GetType().Name == name);

    private static Attribute? GetAttribute(MemberInfo m, string name) =>
        m.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == name);

    /// <summary>
    /// Read the same bundled references as installed help, not a second example registry.
    /// </summary>
    private static Dictionary<string, string[]> LoadExamplesTable(Dictionary<string, CommandSignature> commands)
    {
        using var stream = CommandHelpReference.OpenResource("index.json");
        using var index = JsonDocument.Parse(stream);
        var examples = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in index.RootElement.EnumerateArray())
        {
            var command = entry.GetProperty("command").GetString()!;
            if (!commands.ContainsKey(command))
                continue;
            using var reference = CommandHelpReference.OpenResource(entry.GetProperty("path").GetString()!);
            using var reader = new StreamReader(reference);
            var lines = new List<string>();
            var include = false;
            var fenced = false;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    include = line[3..].Trim() == "Examples";
                    fenced = false;
                }
                if (!include)
                    continue;
                var trimmed = line.Trim();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    fenced = !fenced;
                    continue;
                }
                if (trimmed.StartsWith("$ ", StringComparison.Ordinal))
                    trimmed = trimmed[2..];
                if (fenced && trimmed.StartsWith("twig ", StringComparison.Ordinal))
                    lines.Add(trimmed);
            }
            if (lines.Count > 0)
                examples.Add(command, [.. lines]);
        }
        return examples;
    }

    /// <summary>
    /// Bare shell operators. The shell consumes the operator and its target before argv is
    /// built, so neither reaches the command. Matched only as WHOLE, UNQUOTED tokens: a
    /// placeholder such as <c>&lt;digest&gt;</c> and a quoted <c>"&gt;"</c> stay ordinary values.
    /// </summary>
    private static readonly HashSet<string> ShellOperators =
        new(StringComparer.Ordinal) { ">", ">>", "<", "2>", "2>>", "|", "&&", "||", ";", "#" };

    private static string[] Tokenize(string invocation, string commandName)
    {
        var all = SplitRespectingQuotes(invocation);
        // Without this cut, `--proposal-only > repair.json` would count 'repair.json' as a
        // positional the command never receives.
        var operatorAt = Array.FindIndex(all, t => !t.Quoted && ShellOperators.Contains(t.Text));
        if (operatorAt >= 0)
            all = all[..operatorAt];
        // Drop "twig" plus the command's own words ("link parent" == 2 words).
        var skip = 1 + commandName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return all.Length <= skip ? [] : [.. all[skip..].Select(t => t.Text)];
    }

    private static (string Text, bool Quoted)[] SplitRespectingQuotes(string s)
    {
        var tokens = new List<(string, bool)>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var quoted = false;

        foreach (var c in s)
        {
            if ((c is '"' or '\'') && (quote == '\0' || quote == c))
            {
                quote = quote == '\0' ? c : '\0';
                quoted = true;
                continue;
            }
            if (char.IsWhiteSpace(c) && quote == '\0')
            {
                if (current.Length > 0 || quoted) { tokens.Add((current.ToString(), quoted)); current.Clear(); }
                quoted = false;
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0 || quoted) tokens.Add((current.ToString(), quoted));
        return [.. tokens];
    }

    /// <summary>
    /// Counts positional tokens, consuming each option's VALUE so it is not miscounted.
    /// </summary>
    /// <remarks>
    /// 🔴 This consumption step is load-bearing and was got wrong first time. Without it,
    /// <c>--id 66</c> reads as one flag plus one positional, and the check reports EVERY
    /// dependency verb as lying — 7 false positives against 3 real defects. A guard that
    /// cries wolf on the honest majority gets switched off, so the naive version is worse
    /// than none. Boolean flags take no value, hence the lookahead rather than a blind skip.
    /// </remarks>
    private static int CountPositionals(string[] tokens, CommandSignature signature)
    {
        var count = 0;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!IsFlag(token))
            {
                count++;
                continue;
            }

            // A reflected bool option is a switch, and `--name=value` carries its own value;
            // neither consumes the next token, so a stray word after one stays a positional.
            if (token.Contains('=') || IsBooleanOption(token, signature))
                continue;

            // Consume a following value only when one is present and is not itself a flag.
            if (i + 1 < tokens.Length && !IsFlag(tokens[i + 1]))
                i++;
        }

        return count;
    }

    /// <summary>
    /// A flag is <c>--long</c> or <c>-o</c>. 🔴 Short forms matter: <c>-o json</c> appears
    /// throughout the table, and treating '-o' as a positional (and 'json' as another)
    /// manufactured most of the first run's false failures.
    /// </summary>
    private static bool IsFlag(string token) =>
        token.StartsWith('-') && token.Length > 1;

    private static bool IsBooleanOption(string token, CommandSignature signature) =>
        token.StartsWith("--", StringComparison.Ordinal) && signature.BooleanOptions.Contains(token[2..]);

    private static IEnumerable<string> ExtractFlags(string[] tokens) =>
        tokens.Where(t => t.StartsWith("--", StringComparison.Ordinal))
              .Select(t => t[2..])
              .Select(t => t.Split('=')[0])
              .Where(t => t.Length > 0);

    private static string ToKebabCase(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
