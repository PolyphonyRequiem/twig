using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class GroupedHelpTests
{
    [Fact]
    public void GroupedHelp_ShowsCorrectCommands()
    {
        var output = CaptureHelp();

        output.ShouldNotContain("  save ");     // [Hidden] — must not appear
        output.ShouldContain("  sync ");
        output.ShouldContain("  seed new ");    // canonical seed command
    }

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

        // Methods documented inline as "(alias: ...)" on another entry
        var aliases = new HashSet<string> { "Ws" };

        var nonHidden = new List<string>();
        var hidden = new List<string>();

        DiscoverCommands(typeof(TwigCommands), prefix: null, aliases, nonHidden, hidden);
        DiscoverCommands(typeof(OhMyPoshCommands), prefix: "ohmyposh", aliases, nonHidden, hidden);

        // Sanity: we should discover a meaningful number of commands
        nonHidden.Count.ShouldBeGreaterThan(40);

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

    private static string CaptureHelp()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try { GroupedHelp.Show(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }
}
