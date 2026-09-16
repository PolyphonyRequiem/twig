using System.Reflection;
using System.Text.Json;
using Shouldly;
using Twig.Commands;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ProgressiveHelpTests
{
    [Theory]
    [InlineData("show", "--help", true)]
    [InlineData("seed", "-h", true)]
    [InlineData("help", "show", true)]
    [InlineData("show", "123", false)]
    [InlineData("note", "--", false)]
    public void OnlyExplicitHelpRequestsAreIntercepted(string first, string second, bool expected)
        => ProgressiveHelp.IsHelpRequest([first, second]).ShouldBe(expected);

    [Fact]
    public void EscapedHelpToken_IsData()
        => ProgressiveHelp.IsHelpRequest(["note", "--", "--help"]).ShouldBeFalse();

    [Fact]
    public void HelpBeforeDoubleDash_IsStillHelp()
        => ProgressiveHelp.IsHelpRequest(["show", "--help", "--", "123"]).ShouldBeTrue();

    [Fact]
    public void ShowUnknown_EmitsCompactMessageAndCitesBothHelpEntries()
    {
        using var stderr = new StringWriter();
        ProgressiveHelp.ShowUnknown("bogus", stderr);
        var output = stderr.ToString();
        output.ShouldContain("Unknown command: 'bogus'");
        output.ShouldContain("twig --help");
        output.ShouldContain("twig --help-all");
        // Unknown commands must route discovery, not dump the full catalog.
        output.ShouldNotContain("Getting Started:");
        output.ShouldNotContain("Workspace:");
        output.ShouldNotContain("Proposals:");
        // Two-line ceiling defends against future callers appending a paste of the catalog.
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBeLessThanOrEqualTo(2);
    }


    [Fact]
    public void CatalogParser_PreservesCompoundPathsAliasesAndUnpaddedRows()
    {
        var rows = ProgressiveHelp.ReadCatalog("""
Usage: [command] [-h|--help]

Commands:
  process                           Describe process.
  proposal apply, plan apply        Apply a proposal.
  workspace area add                Add an area path.
  undocumented
""");
        rows.Count.ShouldBe(4);
        rows[1].Names.ShouldBe(["proposal apply", "plan apply"]);
        rows[1].Description.ShouldBe("Apply a proposal.");
        rows[2].Names.ShouldBe(["workspace area add"]);
        rows[3].Names.ShouldBe(["undocumented"]);
    }

    [Fact]
    public void NarrativeExtraction_PreservesConditionalEffectsAndFailuresWithoutDuplicatingSyntax()
    {
        var extracted = CommandHelpReference.ExtractNarrative("""
---
mutates: none
---
## Flags
--old-flag obsolete syntax
## Behavior
Cache only unless --refresh is supplied; --refresh writes local cache.
### Conditional effects
Never flush pending edits.
## Examples
outdated example
## Exit codes and failure modes
| Missing item | 1 |
## See also
other-command
""");
        extracted.ShouldContain("--refresh writes local cache");
        extracted.ShouldContain("Never flush pending edits.");
        extracted.ShouldContain("| Missing item | 1 |");
        extracted.ShouldNotContain("--old-flag");
        extracted.ShouldNotContain("outdated example");
        extracted.ShouldNotContain("mutates: none");
        extracted.ShouldNotContain("other-command");
    }

    [Fact]
    public void EveryCanonicalCommand_HasBundledBehaviorAndFailureSections()
    {
        using var stream = CommandHelpReference.OpenResource("index.json");
        using var index = JsonDocument.Parse(stream);
        var entries = index.RootElement.EnumerateArray().ToDictionary(
            entry => entry.GetProperty("command").GetString()!,
            entry => entry.GetProperty("path").GetString()!, StringComparer.Ordinal);
        var commands = CanonicalCommands(typeof(TwigCommands), "")
            .Concat(CanonicalCommands(typeof(OhMyPoshCommands), "ohmyposh "))
            .Concat(CanonicalCommands(typeof(SkillCommands), "skills ")).ToArray();
        foreach (var command in commands)
        {
            entries.ShouldContainKey(command);
            using var document = CommandHelpReference.OpenResource(entries[command]);
            using var reader = new StreamReader(document);
            var narrative = CommandHelpReference.ExtractNarrative(reader.ReadToEnd());
            narrative.ShouldContain("## Behavior", customMessage: command);
            narrative.ShouldContain("## Exit codes", customMessage: command);
        }
    }


    private static IEnumerable<string> CanonicalCommands(Type type, string prefix)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.CustomAttributes.Any(a => a.AttributeType.Name == "HiddenAttribute")) continue;
            var commandAttribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "CommandAttribute");
            var raw = commandAttribute is null ? method.Name.ToLowerInvariant()
                : (string)commandAttribute.ConstructorArguments[0].Value!;
            yield return prefix + raw.Split('|')[0];
        }
    }
}
