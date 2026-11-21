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
        var output = CaptureShowIfPresent(["tree"],
            ("tree", ["twig tree    Show work item hierarchy"]));

        output.ShouldContain("Show work item hierarchy");
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
        // "show-batch" is a single hyphenated token
        var output = CaptureShowIfPresent(["show-batch", "--help"],
            ("show-batch", ["twig show-batch           Batch lookup work items"]));

        output.ShouldContain("Batch lookup work items");
    }

    [Fact]
    public void ShowIfPresent_OutputFormat_IncludesExamplesHeader()
    {
        var output = CaptureShowIfPresent(["show"],
            ("show", ["twig show 1234             Show one item",
                      "twig show 1234 --tree      Show its tree"]));

        output.ShouldContain("Examples:");
        // Examples should be indented with two spaces
        output.ShouldContain("  twig show 1234 ");
        output.ShouldContain("  twig show 1234 --tree");
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
        var output = CaptureShowIfPresent(["show"],
            ("show", ["twig show 1234             Show one item",
                      "twig show 1234 --tree      Show its tree"]));

        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        // "Examples:" header + 2 example lines
        lines.Count(l => l.TrimStart().StartsWith("twig")).ShouldBe(2);
    }

    [Fact]
    public void ShowIfPresent_TripleCompoundCommand_MatchesThreeTokenKeyFirst()
    {
        // "workspace area add" should match the 3-token key, not "workspace area" or "workspace"
        var output = CaptureShowIfPresent(["workspace", "area", "add", "--help"],
            ("workspace area add", ["twig workspace area add \"P\\T\"   Add area path"]),
            ("workspace area", ["twig workspace area               Show area view"]),
            ("workspace", ["twig workspace                     Show workspace"]));

        output.ShouldContain("Add area path");
        output.ShouldNotContain("Show area view");
        output.ShouldNotContain("Show workspace");
    }

    [Fact]
    public void ShowIfPresent_TripleCompoundFallsBackToDouble_WhenTripleNotFound()
    {
        var output = CaptureShowIfPresent(["bench", "rules", "Focused"],
            ("bench rules", ["twig bench rules Focused           Show named rules"]),
            ("bench", ["twig bench list                        List benches"]));

        output.ShouldContain("Show named rules");
        output.ShouldNotContain("List benches");
    }

    [Fact]
    public void WorkspaceAreaHelp_OnlyAdvertisesSupportedSync()
    {
        CommandExamples.Examples["workspace area sync"].Length.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var retired in new[] { "workspace area add", "workspace area remove", "workspace area list" })
            CommandExamples.Examples.ShouldNotContainKey(retired);
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
