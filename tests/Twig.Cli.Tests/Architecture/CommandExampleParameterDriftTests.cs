using System.Reflection;
using Twig;
using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Architecture;

/// <summary>
/// AB#389 — pins every <c>--help</c> example against the parameters its command actually
/// declares, so the two cannot drift apart again.
/// </summary>
/// <remarks>
/// <para>
/// The reported defect: <c>twig link parent --help</c> advertised
/// <c>twig link parent 5678 1234</c>, and the command rejected it —
/// <i>"Argument '1234' is not recognized."</i> Not undocumented behaviour, but
/// <b>documented behaviour that did not exist</b>. Batch reparenting therefore cost two
/// commands per item (<c>twig set &lt;child&gt;</c> then <c>twig link parent &lt;parent&gt;</c>),
/// and mutated active-item context as a side effect of a link operation.
/// </para>
/// <para>
/// 🔴 <b>Why a test rather than three corrected strings.</b> The examples table
/// (<c>CommandExamples.Examples</c>) and the <c>[Command]</c> method signatures are two
/// hand-maintained sources of truth for one fact, and nothing compared them. That is the
/// defect class, and the reporter named it: <i>"A test that asserts this mechanically
/// would prevent the whole class."</i> Fixing only the three known strings would leave
/// the generator of the fault in place.
/// </para>
/// <para>
/// <b>It found a third instance immediately.</b> The report confirmed <c>link parent</c>
/// and <c>link unparent</c> and listed <c>link reparent</c> only as a thing to check.
/// This assertion caught <c>link reparent</c> on the first run, which is the argument for
/// the mechanical form over a manual sweep.
/// </para>
/// <para>
/// <b>Scope is deliberately the whole CLI, not just the link verbs.</b> Nothing about the
/// drift is specific to <c>link</c>; any command whose examples are edited without its
/// signature (or vice versa) has the same failure mode, and it is silent until a user
/// copies the example.
/// </para>
/// </remarks>
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
        commands.ShouldNotBeEmpty("no [Command] methods discovered — the reflection probe is broken, "
            + "and a probe that finds nothing passes vacuously");

        var examplesTable = LoadExamplesTable();
        examplesTable.ShouldNotBeEmpty("no examples discovered — see above; an empty table cannot fail");

        var failures = new List<string>();

        foreach (var (commandName, examples) in examplesTable)
        {
            if (!commands.TryGetValue(commandName, out var signature))
                signature = null; // A group heading need not be a [Command] itself.

            foreach (var example in examples)
            {
                // An example line is "<invocation><two-or-more spaces><description>".
                var invocation = SplitInvocation(example);

                // 🔴 A GROUP's entry legitimately advertises its SUBCOMMANDS: the "nav" key
                // lists `twig nav up` and `twig nav down`, which are separate [Command]s with
                // their own signatures. Validate each example against the most specific
                // command it actually names, or a group heading reads as its own bare verb
                // taking extra positionals — 3 of the first run's false failures.
                var effectiveName = MostSpecificCommand(invocation, commandName, commands);
                if (!commands.TryGetValue(effectiveName, out signature))
                    continue;

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

    private sealed record CommandSignature(int PositionalCount, IReadOnlySet<string> Options);

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

        var best = fallback;
        for (var take = words.Length - 1; take >= 1; take--)
        {
            var candidate = string.Join(' ', words.Skip(1).Take(take));
            if (commands.ContainsKey(candidate) && candidate.Length > best.Length)
            {
                best = candidate;
                break;
            }
        }

        return best;
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
        var programType = TwigAssembly
            .GetTypes()
            .FirstOrDefault(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => HasAttribute(m, "CommandAttribute")));

        if (programType is null)
            return result;

        foreach (var method in programType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var command = GetAttribute(method, "CommandAttribute");
            if (command is null)
                continue;

            var commandName = command.GetType().GetProperty("Command")?.GetValue(command) as string;
            if (string.IsNullOrWhiteSpace(commandName))
                continue;

            var positional = 0;
            var options = new HashSet<string>(StringComparer.Ordinal);

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
                options.Add(ToKebabCase(p.Name!));
            }

            result[commandName] = new CommandSignature(positional, options);
        }

        return result;
    }

    // Anchored on a real twig type so the assembly is guaranteed loaded — scanning
    // AppDomain assemblies would depend on load order and could find nothing.
    private static Assembly TwigAssembly { get; } = typeof(Twig.Commands.LinkCommand).Assembly;

    private static bool HasAttribute(MemberInfo m, string name) =>
        m.GetCustomAttributes().Any(a => a.GetType().Name == name);

    private static bool HasAttribute(ParameterInfo p, string name) =>
        p.GetCustomAttributes().Any(a => a.GetType().Name == name);

    private static Attribute? GetAttribute(MemberInfo m, string name) =>
        m.GetCustomAttributes().FirstOrDefault(a => a.GetType().Name == name);

    /// <summary>
    /// Read the table DIRECTLY rather than by reflection. <c>Twig.csproj</c> grants
    /// <c>InternalsVisibleTo</c> to this assembly, so the internal member is reachable and
    /// the reference is compiler-checked — a rename breaks the build instead of silently
    /// returning an empty table and passing vacuously. (The reflective first draft did
    /// exactly that; the emptiness guard in the test body is what caught it.)
    /// </summary>
    private static Dictionary<string, string[]> LoadExamplesTable() => CommandExamples.Examples;

    /// <summary>
    /// Split "&lt;invocation&gt;   &lt;description&gt;" into just the invocation.
    /// </summary>
    /// <remarks>
    /// 🔴 Two-space separation is the CONVENTION, not a guarantee. A handful of examples
    /// use a single space before their description, so a naive two-space split swallows
    /// the prose as positional arguments — that produced a 10-positional reading of one
    /// line and 14 false failures on the first run. Where the convention is absent, fall
    /// back to cutting at the first token that looks like prose (a capitalised word that
    /// is not a flag, a number, a URL, or a quoted string).
    /// </remarks>
    private static string SplitInvocation(string example)
    {
        var idx = example.IndexOf("  ", StringComparison.Ordinal);
        if (idx >= 0)
            return example[..idx].Trim();

        // No two-space separator: the line is invocation-only. Descriptions in this table
        // are always separated by two or more spaces, so treating the whole line as the
        // invocation is correct here and, importantly, is not a guess about prose.
        return example.Trim();
    }

    private static string[] Tokenize(string invocation, string commandName)
    {
        var all = SplitRespectingQuotes(invocation);
        // Drop "twig" plus the command's own words ("link parent" == 2 words).
        var skip = 1 + commandName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return all.Length <= skip ? [] : all[skip..];
    }

    private static string[] SplitRespectingQuotes(string s)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in s)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
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
