using Shouldly;
using Xunit;

namespace Twig.Cli.Tests;

public sealed class GroupedHelpTests
{
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
}
