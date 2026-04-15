using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class JoinTrailingTextTests
{
    // --- Named parameter precedence (backward compat: --text / --title) ---

    [Fact]
    public void Named_provided_returns_named()
    {
        var result = TwigCommands.JoinTrailingText("explicit text", ["trailing", "words"]);
        result.ShouldBe("explicit text");
    }

    [Fact]
    public void Named_with_empty_positional_returns_named()
    {
        // twig note --text "my note text" (no trailing args)
        var result = TwigCommands.JoinTrailingText("my note text", []);
        result.ShouldBe("my note text");
    }

    [Fact]
    public void Named_with_null_positional_returns_named()
    {
        // twig new --title "Fix the bug" (params array omitted)
        var result = TwigCommands.JoinTrailingText("Fix the bug", null);
        result.ShouldBe("Fix the bug");
    }

    [Fact]
    public void Named_empty_string_returns_empty_string()
    {
        // Named takes precedence even if it's empty — caller decides semantics
        var result = TwigCommands.JoinTrailingText("", ["positional"]);
        result.ShouldBe("");
    }

    [Fact]
    public void Named_whitespace_only_returns_whitespace()
    {
        // Whitespace-only named param is still "provided" — not collapsed to null
        var result = TwigCommands.JoinTrailingText("  ", ["positional", "words"]);
        result.ShouldBe("  ");
    }

    // --- Positional args joining (trailing text) ---

    [Fact]
    public void Named_null_joins_positional()
    {
        var result = TwigCommands.JoinTrailingText(null, ["Hello", "world"]);
        result.ShouldBe("Hello world");
    }

    [Theory]
    [InlineData(new[] { "Fix", "the", "bug" }, "Fix the bug")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "one", "two", "three", "four", "five" }, "one two three four five")]
    [InlineData(new[] { "WIP" }, "WIP")]
    [InlineData(new[] { "Done:", "all", "tests", "pass" }, "Done: all tests pass")]
    public void Positional_tokens_joined_with_single_space(string[] tokens, string expected)
    {
        var result = TwigCommands.JoinTrailingText(null, tokens);
        result.ShouldBe(expected);
    }

    [Fact]
    public void Positional_preserves_punctuation_and_symbols()
    {
        // twig note Fix: issue #123 (urgent)
        var result = TwigCommands.JoinTrailingText(null, ["Fix:", "issue", "#123", "(urgent)"]);
        result.ShouldBe("Fix: issue #123 (urgent)");
    }

    // --- Null/empty cases ---

    [Fact]
    public void Both_null_returns_null()
    {
        var result = TwigCommands.JoinTrailingText(null, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void Named_null_empty_positional_returns_null()
    {
        // twig note (no args) → null triggers editor flow
        var result = TwigCommands.JoinTrailingText(null, []);
        result.ShouldBeNull();
    }
}
