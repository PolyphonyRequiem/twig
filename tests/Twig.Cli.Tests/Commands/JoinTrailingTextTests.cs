using Shouldly;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class JoinTrailingTextTests
{
    [Fact]
    public void Named_provided_returns_named()
    {
        var result = TwigCommands.JoinTrailingText("explicit text", ["trailing", "words"]);
        result.ShouldBe("explicit text");
    }

    [Fact]
    public void Named_null_joins_positional()
    {
        var result = TwigCommands.JoinTrailingText(null, ["Hello", "world"]);
        result.ShouldBe("Hello world");
    }

    [Fact]
    public void Both_null_returns_null()
    {
        var result = TwigCommands.JoinTrailingText(null, null);
        result.ShouldBeNull();
    }

    [Fact]
    public void Named_null_empty_positional_returns_null()
    {
        var result = TwigCommands.JoinTrailingText(null, []);
        result.ShouldBeNull();
    }

    [Fact]
    public void Single_positional_word_returns_as_is()
    {
        var result = TwigCommands.JoinTrailingText(null, ["hello"]);
        result.ShouldBe("hello");
    }

    [Fact]
    public void Multiple_positional_words_joined_with_spaces()
    {
        var result = TwigCommands.JoinTrailingText(null, ["starting", "work", "on", "feature"]);
        result.ShouldBe("starting work on feature");
    }

    [Fact]
    public void Named_empty_string_returns_empty_string()
    {
        // Named takes precedence even if it's empty — caller decides semantics
        var result = TwigCommands.JoinTrailingText("", ["positional"]);
        result.ShouldBe("");
    }
}
