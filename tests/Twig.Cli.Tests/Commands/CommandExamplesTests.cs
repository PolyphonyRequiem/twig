using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class CommandExamplesTests
{
    [Fact]
    public void ShowIfPresent_CompoundCommand_MatchesCompoundKeyFirst()
    {
        // Arrange — temporarily inject known examples
        var output = CaptureShowIfPresent(["nav", "up", "--help"],
            ("nav up", ["twig nav up    Navigate to parent work item"]),
            ("nav", ["twig nav         List navigation sub-commands"]));

        // Should match "nav up", not "nav"
        output.ShouldContain("Navigate to parent work item");
        output.ShouldNotContain("List navigation sub-commands");
    }

    [Fact]
    public void ShowIfPresent_FallsBackToFirstArg_WhenCompoundNotFound()
    {
        // "set 1234" compound key won't match, falls back to "set"
        var output = CaptureShowIfPresent(["set", "1234", "--help"],
            ("set", ["twig set 1234              Set context by work item ID"]));

        output.ShouldContain("Set context by work item ID");
    }

    [Fact]
    public void ShowIfPresent_SingleArg_MatchesDirectly()
    {
        var output = CaptureShowIfPresent(["status"],
            ("status", ["twig status    Show current work item status"]));

        output.ShouldContain("Show current work item status");
    }

    [Fact]
    public void ShowIfPresent_NoMatch_ProducesNoOutput()
    {
        var output = CaptureShowIfPresent(["nonexistent"],
            ("set", ["twig set 1234              Set context by work item ID"]));

        output.ShouldBeEmpty();
    }

    [Fact]
    public void ShowIfPresent_EmptyArgs_ProducesNoOutput()
    {
        var output = CaptureShowIfPresent([],
            ("set", ["twig set 1234              Set context by work item ID"]));

        output.ShouldBeEmpty();
    }

    [Fact]
    public void ShowIfPresent_HyphenatedCommand_MatchesViaSingleToken()
    {
        // "flow-start" is a single hyphenated token
        var output = CaptureShowIfPresent(["flow-start", "--help"],
            ("flow-start", ["twig flow-start           Start a flow"]));

        output.ShouldContain("Start a flow");
    }

    [Fact]
    public void ShowIfPresent_OutputFormat_IncludesExamplesHeader()
    {
        var output = CaptureShowIfPresent(["set"],
            ("set", ["twig set 1234              Set context by work item ID",
                     "twig set \"login page\"      Set context by title match"]));

        output.ShouldContain("Examples:");
        // Examples should be indented with two spaces
        output.ShouldContain("  twig set 1234");
        output.ShouldContain("  twig set \"login page\"");
    }

    [Fact]
    public void SeedPublishExamples_ContainLinkBranchUsage()
    {
        CommandExamples.Examples.TryGetValue("seed publish", out var examples).ShouldBeTrue(
            "seed publish should have registered examples");
        examples.ShouldNotBeNull();
        examples.Any(e => e.Contains("--link-branch")).ShouldBeTrue(
            "seed publish examples should include a --link-branch example");
    }

    [Fact]
    public void ShowIfPresent_MultipleExamples_PrintsAll()
    {
        var output = CaptureShowIfPresent(["set"],
            ("set", ["twig set 1234              Set by ID",
                     "twig set \"login page\"      Set by title"]));

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // "Examples:" header + 2 example lines
        lines.Count(l => l.TrimStart().StartsWith("twig")).ShouldBe(2);
    }

    /// <summary>
    /// Helper that temporarily replaces <see cref="CommandExamples.Examples"/> with test data,
    /// captures console output from <see cref="CommandExamples.ShowIfPresent"/>, and restores the
    /// original dictionary afterwards.
    /// </summary>
    private static string CaptureShowIfPresent(
        string[] args,
        params (string Key, string[] Values)[] testExamples)
    {
        // Snapshot and clear the real dictionary
        var original = new Dictionary<string, string[]>(CommandExamples.Examples);
        CommandExamples.Examples.Clear();
        foreach (var (key, values) in testExamples)
            CommandExamples.Examples[key] = values;

        var origOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            CommandExamples.ShowIfPresent(args);
        }
        finally
        {
            Console.SetOut(origOut);
            // Restore original examples
            CommandExamples.Examples.Clear();
            foreach (var kvp in original)
                CommandExamples.Examples[kvp.Key] = kvp.Value;
        }
        return writer.ToString();
    }
}
