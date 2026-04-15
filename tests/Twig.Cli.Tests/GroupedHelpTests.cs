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

    [Fact]
    public void ShowUnknown_WritesErrorToStderrAndHelpToStdout()
    {
        var (stderr, stdout) = CaptureShowUnknown("frobnicate");

        stderr.ShouldContain("Unknown command: 'frobnicate'");
        stdout.ShouldContain("Usage: twig");
        stdout.ShouldContain("Getting Started:");
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
