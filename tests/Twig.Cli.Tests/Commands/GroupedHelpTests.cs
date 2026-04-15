using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class GroupedHelpTests
{
    [Fact]
    public void BareSeed_HasHiddenAttribute()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.Seed),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull("TwigCommands.Seed method not found");
        // ConsoleAppFramework source-generates a local HiddenAttribute in each project,
        // so we match by name rather than by type to avoid CS0436 ambiguity.
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeTrue("Bare Seed() should have [Hidden] since 'seed new' is the canonical command");
    }

    [Fact]
    public void SeedNew_DoesNotHaveHiddenAttribute()
    {
        var method = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.SeedNew),
            BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull("TwigCommands.SeedNew method not found");
        method.GetCustomAttributes()
            .Any(a => a.GetType().Name == "HiddenAttribute")
            .ShouldBeFalse("SeedNew should NOT be hidden — it is the canonical seed command");
    }

    [Fact]
    public void AllNonHiddenCommands_AppearInGroupedHelp()
    {
        var helpOutput = CaptureHelp();
        var (nonHidden, hidden) = GetCommands();

        // Every non-hidden command must be in KnownCommands
        var missingFromKnown = nonHidden
            .Where(cmd => !GroupedHelp.KnownCommands.Contains(cmd))
            .ToList();
        missingFromKnown.ShouldBeEmpty(
            $"Non-hidden commands missing from KnownCommands: {string.Join(", ", missingFromKnown)}");

        // Every non-hidden command must appear in the help output
        var missingFromOutput = nonHidden
            .Where(cmd => !helpOutput.Contains(cmd))
            .ToList();
        missingFromOutput.ShouldBeEmpty(
            $"Non-hidden commands missing from Show() output: {string.Join(", ", missingFromOutput)}");

        // No hidden command should appear as a standalone entry in help output.
        // Pattern: command name at line start (indented) followed by 2+ spaces
        // (description padding). This avoids false positives from compound commands
        // like "seed new" when checking bare "seed".
        var leakedHidden = hidden
            .Where(cmd => Regex.IsMatch(helpOutput, $@"(?m)^\s+{Regex.Escape(cmd)}\s{{2,}}"))
            .ToList();
        leakedHidden.ShouldBeEmpty(
            $"Hidden commands leaked into Show() output: {string.Join(", ", leakedHidden)}");
    }

    [Fact]
    public void AllCommands_HaveExamples()
    {
        var (nonHidden, _) = GetCommands();

        // Every non-hidden command must have at least one example
        var missingExamples = nonHidden
            .Where(cmd => !CommandExamples.Examples.ContainsKey(cmd))
            .ToList();
        missingExamples.ShouldBeEmpty(
            $"Non-hidden commands missing from CommandExamples: {string.Join(", ", missingExamples)}");

        // Every example entry must contain at least one line
        var emptyExamples = nonHidden
            .Where(cmd => CommandExamples.Examples.TryGetValue(cmd, out var ex) && ex.Length == 0)
            .ToList();
        emptyExamples.ShouldBeEmpty(
            $"Commands with empty example arrays: {string.Join(", ", emptyExamples)}");

        // Every individual example line must be non-whitespace
        var blankLineCommands = nonHidden
            .Where(cmd => CommandExamples.Examples.TryGetValue(cmd, out var ex)
                && ex.Any(string.IsNullOrWhiteSpace))
            .ToList();
        blankLineCommands.ShouldBeEmpty(
            $"Commands with blank example lines: {string.Join(", ", blankLineCommands)}");

        // No orphaned entries: every key in Examples must map to a known non-hidden command
        var orphanedExamples = CommandExamples.Examples.Keys
            .Where(key => !nonHidden.Contains(key))
            .ToList();
        orphanedExamples.ShouldBeEmpty(
            $"CommandExamples contains entries for unknown/hidden commands: {string.Join(", ", orphanedExamples)}");
    }

    private static (List<string> NonHidden, List<string> Hidden) GetCommands()
    {
        var aliases = new HashSet<string> { "Ws" };
        var nonHidden = new List<string>();
        var hidden = new List<string>();
        DiscoverCommands(typeof(TwigCommands), prefix: null, aliases, nonHidden, hidden);
        DiscoverCommands(typeof(OhMyPoshCommands), prefix: "ohmyposh", aliases, nonHidden, hidden);
        nonHidden.Count.ShouldBeGreaterThan(40);
        return (nonHidden, hidden);
    }

    private static void DiscoverCommands(
        Type commandsType,
        string? prefix,
        HashSet<string> aliases,
        List<string> nonHidden,
        List<string> hidden)
    {
        var methods = commandsType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var isHidden = method.GetCustomAttributes()
                .Any(a => a.GetType().Name == "HiddenAttribute");

            // Derive CLI name: [Command("name")] if present, else lowercase method name
            var cmdAttrData = method.CustomAttributes
                .FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");

            var rawName = cmdAttrData is null
                ? method.Name.ToLowerInvariant()
                : (string)cmdAttrData.ConstructorArguments[0].Value!;
            var cliName = prefix is null ? rawName : $"{prefix} {rawName}";

            if (isHidden)
            {
                hidden.Add(cliName);
            }
            else if (aliases.Contains(method.Name))
            {
                // Documented inline on another entry (e.g., "workspace (alias: ws)")
            }
            else
            {
                nonHidden.Add(cliName);
            }
        }
    }

    // Non-hidden commands are validated dynamically by AllNonHiddenCommands_AppearInGroupedHelp.
    // This theory covers only entries that reflection cannot discover:
    // the "help" pseudo-command, hidden backward-compat aliases, and bare group prefixes.
    [Theory]
    [InlineData("help")]          // pseudo-command: no method, handled by early-exit block
    [InlineData("ohmyposh")]      // group prefix: no method in OhMyPoshCommands
    [InlineData("link")]          // group prefix: no standalone handler
    [InlineData("hooks")]         // group prefix: no standalone handler
    // Hidden backward-compat aliases
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("back")]
    [InlineData("fore")]
    [InlineData("history")]
    [InlineData("seed")]
    [InlineData("save")]
    [InlineData("refresh")]
    [InlineData("_hook")]
    public void KnownCommands_ContainsExpectedCommand(string command)
    {
        GroupedHelp.KnownCommands.ShouldContain(command);
    }

    [Fact]
    public void KnownCommands_AllEntriesAreValid()
    {
        foreach (var cmd in GroupedHelp.KnownCommands)
        {
            cmd.ShouldNotBeNullOrWhiteSpace("KnownCommands must not contain null or whitespace entries");
            cmd.ShouldBe(cmd.Trim(), $"Command '{cmd}' has leading or trailing whitespace");
            cmd.ShouldBe(cmd.ToLowerInvariant(), $"Command '{cmd}' should be lowercase (CLI convention)");
        }
    }

    [Fact]
    public void ShowUnknown_WritesErrorToStderrAndHelpToStdout()
    {
        var (stderr, stdout) = CaptureShowUnknown("frobnicate");

        stderr.ShouldContain("Unknown command: 'frobnicate'");
        stdout.ShouldContain("Usage: twig");
        stdout.ShouldContain("Getting Started:");
        stdout.ShouldContain("Views:");
        stdout.ShouldContain("Navigation:");
        stdout.ShouldContain("Work Items:");
        stdout.ShouldContain("Git:");
    }

    [Theory]
    [InlineData("")]
    [InlineData("some-weird-cmd")]
    [InlineData("command with spaces")]
    public void ShowUnknown_IncludesCommandNameInError(string command)
    {
        var (stderr, _) = CaptureShowUnknown(command);

        stderr.ShouldContain($"Unknown command: '{command}'");
    }

    [Theory]
    [InlineData("status")]
    [InlineData("set")]
    [InlineData("help")]
    [InlineData("nav")]
    [InlineData("seed")]
    public void IsKnownCommand_RecognizesTopLevelCommands(string command)
    {
        GroupedHelp.IsKnownCommand([command]).ShouldBeTrue();
    }

    [Theory]
    [InlineData("nav", "up")]
    [InlineData("nav", "down")]
    [InlineData("seed", "new")]
    [InlineData("seed", "edit")]
    [InlineData("link", "parent")]
    [InlineData("hooks", "install")]
    [InlineData("ohmyposh", "init")]
    public void IsKnownCommand_RecognizesCompoundCommands(string first, string second)
    {
        GroupedHelp.IsKnownCommand([first, second]).ShouldBeTrue();
    }

    [Theory]
    [InlineData("foobar")]
    [InlineData("frobnicate")]
    [InlineData("halp")]
    [InlineData("stats")]
    public void IsKnownCommand_ReturnsFalseForUnknownCommands(string command)
    {
        GroupedHelp.IsKnownCommand([command]).ShouldBeFalse();
    }

    [Fact]
    public void IsKnownCommand_ReturnsFalseForEmptyArgs()
    {
        GroupedHelp.IsKnownCommand([]).ShouldBeFalse();
    }

    [Theory]
    [InlineData("set", "123")]
    [InlineData("status", "--all")]
    public void IsKnownCommand_FallsBackToTopLevelWhenCompoundUnknown(string first, string second)
    {
        // "set 123" is not a compound command, but "set" is a top-level command
        GroupedHelp.IsKnownCommand([first, second]).ShouldBeTrue();
    }

    private static string CaptureHelp()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try { GroupedHelp.Show(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }

    private static (string Stderr, string Stdout) CaptureShowUnknown(string command)
    {
        var origErr = Console.Error;
        var origOut = Console.Out;
        using var errWriter = new StringWriter();
        using var outWriter = new StringWriter();
        Console.SetError(errWriter);
        Console.SetOut(outWriter);
        try { GroupedHelp.ShowUnknown(command); }
        finally { Console.SetError(origErr); Console.SetOut(origOut); }
        return (errWriter.ToString(), outWriter.ToString());
    }
}
