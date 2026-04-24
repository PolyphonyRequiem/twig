using Shouldly;
using Twig.Infrastructure.Content;
using Xunit;

namespace Twig.Infrastructure.Tests.Content;

public sealed class FieldAppenderTests
{
    // ── Append: null/empty existing value returns newValue as-is ──

    [Fact]
    public void Append_NullExisting_ReturnsNewValue()
    {
        var result = FieldAppender.Append(null, "new content");

        result.ShouldBe("new content");
    }

    [Fact]
    public void Append_EmptyExisting_ReturnsNewValue()
    {
        var result = FieldAppender.Append("", "new content");

        result.ShouldBe("new content");
    }

    [Fact]
    public void Append_WhitespaceExisting_ReturnsNewValue()
    {
        var result = FieldAppender.Append("   ", "new content");

        result.ShouldBe("new content");
    }

    // ── Append: plain-text to plain-text uses newline separator ──

    [Fact]
    public void Append_PlainTextToPlainText_UsesNewlineSeparator()
    {
        var result = FieldAppender.Append("existing text", "new text");

        result.ShouldBe("existing text\n\nnew text");
    }

    // ── Append: to HTML uses <br><br> separator ──

    [Fact]
    public void Append_ToHtmlContent_UsesBrSeparator()
    {
        var result = FieldAppender.Append("<p>existing</p>", "new text");

        result.ShouldBe("<p>existing</p><br><br>new text");
    }

    [Fact]
    public void Append_HtmlToHtml_UsesBrSeparator()
    {
        var result = FieldAppender.Append(
            "<p>existing</p>",
            "<p>new</p>");

        result.ShouldBe("<p>existing</p><br><br><p>new</p>");
    }

    [Fact]
    public void Append_DivContent_UsesBrSeparator()
    {
        var result = FieldAppender.Append(
            "<div>existing</div>",
            "appended");

        result.ShouldBe("<div>existing</div><br><br>appended");
    }

    // ── LooksLikeHtml ──

    [Theory]
    [InlineData("<p>text</p>")]
    [InlineData("<div>content</div>")]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<ul><li>item</li></ul>")]
    [InlineData("<h1>heading</h1>")]
    [InlineData("<h6>heading</h6>")]
    [InlineData("some text <em>emphasis</em> more text")]
    public void LooksLikeHtml_HtmlContent_ReturnsTrue(string value)
    {
        FieldAppender.LooksLikeHtml(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("no tags here")]
    [InlineData("just some words")]
    [InlineData("")]
    public void LooksLikeHtml_PlainText_ReturnsFalse(string value)
    {
        FieldAppender.LooksLikeHtml(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("less than < but no close")]
    [InlineData("greater than > but no open")]
    public void LooksLikeHtml_PartialAngleBrackets_ReturnsFalse(string value)
    {
        FieldAppender.LooksLikeHtml(value).ShouldBeFalse();
    }

    [Fact]
    public void LooksLikeHtml_BothAngleBracketsPresent_ReturnsTrue()
    {
        FieldAppender.LooksLikeHtml("a < b > c").ShouldBeTrue();
    }
}
