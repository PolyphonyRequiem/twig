using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SubcommandGuardTests
{
    [Theory]
    [InlineData("link")]
    [InlineData("seed")]
    [InlineData("nav")]
    [InlineData("bench rule")]
    [InlineData("workspace sprint")]
    [InlineData("workspace area")]
    public void UnknownSubcommandNamesTheActualGroupAndAvailableVerbs(string group)
    {
        var error = SubcommandGuard.Validate([.. group.Split(' '), "bogus"]);
        error.ShouldNotBeNull().ShouldContain($"Unknown subcommand: 'bogus' is not a '{group}' command.");
        error.ShouldContain($"Valid '{group}' subcommands:");
    }

    [Theory]
    [InlineData("link")]
    [InlineData("seed")]
    [InlineData("nav")]
    [InlineData("auth")]
    [InlineData("proposal")]
    [InlineData("bench rule")]
    public void GroupWithoutBareHandlerRequiresASubcommand(string group)
    {
        SubcommandGuard.Validate(group.Split(' ')).ShouldNotBeNull()
            .ShouldContain($"Missing subcommand: '{group}' requires one.");
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("ws")]
    [InlineData("bench")]
    [InlineData("bench planning")]
    [InlineData("workspace area")]
    public void CurrentBareReadHandlersRemainAvailable(string command) =>
        SubcommandGuard.Validate(command.Split(' ')).ShouldBeNull();

    [Theory]
    [InlineData("set", "123")]
    [InlineData("show", "77")]
    [InlineData("process", "A discovered type")]
    [InlineData("note", "a message")]
    public void PositionalValuesAreNotGroupVerbs(string command, string value) =>
        SubcommandGuard.Validate([command, value]).ShouldBeNull();

    [Theory]
    [InlineData("bench planning refresh")]
    [InlineData("bench rule add")]
    [InlineData("workspace area add")]
    [InlineData("proposal preview")]
    [InlineData("seed new")]
    [InlineData("link local related")]
    public void CurrentCompoundHandlersRemainAvailable(string command) =>
        SubcommandGuard.Validate(command.Split(' ')).ShouldBeNull();

    [Fact]
    public void HelpAndUnknownTopLevelCommandsAreOwnedByEarlierGuards()
    {
        SubcommandGuard.Validate(["link", "--help"]).ShouldBeNull();
        SubcommandGuard.Validate(["unknown", "verb"]).ShouldBeNull();
        SubcommandGuard.Validate([]).ShouldBeNull();
    }
}
