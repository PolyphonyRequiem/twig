using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

public sealed class GroupedHelpTests
{
    [Theory]
    [InlineData("set")]
    [InlineData("nav")]
    [InlineData("nav up")]
    [InlineData("nav down")]
    [InlineData("nav next")]
    [InlineData("nav prev")]
    [InlineData("nav back")]
    [InlineData("nav fore")]
    [InlineData("nav history")]
    [InlineData("seed new")]
    [InlineData("seed edit")]
    [InlineData("seed discard")]
    [InlineData("seed view")]
    [InlineData("seed link")]
    [InlineData("seed unlink")]
    [InlineData("seed links")]
    [InlineData("seed chain")]
    [InlineData("seed validate")]
    [InlineData("seed publish")]
    [InlineData("seed reconcile")]
    [InlineData("link parent")]
    [InlineData("link unparent")]
    [InlineData("link reparent")]
    [InlineData("hooks install")]
    [InlineData("hooks uninstall")]
    [InlineData("config status-fields")]
    [InlineData("stash pop")]
    [InlineData("ohmyposh")]
    [InlineData("ohmyposh init")]
    [InlineData("help")]
    [InlineData("query")]
    [InlineData("new")]
    [InlineData("flow-start")]
    [InlineData("flow-done")]
    [InlineData("flow-close")]
    // Hidden backward-compat aliases (still accepted by the CLI)
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
    // Bare group prefixes for compound commands
    [InlineData("link")]
    [InlineData("hooks")]
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
}
