using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class JoinTrailingTextTests
{
    // Named parameter always wins (backward compat: --text / --title)

    [Fact]
    public void Named_nonnull_wins_over_positional()
        => TwigCommands.JoinTrailingText("explicit text", ["trailing", "words"]).ShouldBe("explicit text");

    [Fact]
    public void Named_nonnull_wins_when_positional_null()
        => TwigCommands.JoinTrailingText("Fix the bug", null).ShouldBe("Fix the bug");

    // Positional tokens joined with a single space

    [Theory]
    [InlineData(new[] { "Fix", "the", "bug" }, "Fix the bug")]
    [InlineData(new[] { "WIP" }, "WIP")]
    [InlineData(new[] { "one", "two", "three", "four", "five" }, "one two three four five")]
    [InlineData(new[] { "Done:", "all", "tests", "pass" }, "Done: all tests pass")]
    [InlineData(new[] { "Fix:", "issue", "#123", "(urgent)" }, "Fix: issue #123 (urgent)")]
    public void Positional_tokens_joined_with_single_space(string[] tokens, string expected)
        => TwigCommands.JoinTrailingText(null, tokens).ShouldBe(expected);

    // Returns null when nothing provided — null triggers editor flow in callers

    [Fact]
    public void Both_null_returns_null()
        => TwigCommands.JoinTrailingText(null, null).ShouldBeNull();

    [Fact]
    public void Null_named_empty_positional_returns_null()
        => TwigCommands.JoinTrailingText(null, []).ShouldBeNull();
}
