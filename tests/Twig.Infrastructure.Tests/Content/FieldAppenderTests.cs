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

    // ── Append: plain text appended to HTML wraps in <p> ──

    [Fact]
    public void Append_PlainTextToHtml_WrapsInParagraphTag()
    {
        var result = FieldAppender.Append("<p>existing</p>", "new text");

        result.ShouldBe("<p>existing</p><p>new text</p>");
    }

    [Fact]
    public void Append_HtmlToHtml_ConcatenatesDirectly()
    {
        var result = FieldAppender.Append(
            "<p>existing</p>",
            "<p>new</p>");

        result.ShouldBe("<p>existing</p><p>new</p>");
    }

    [Fact]
    public void Append_PlainTextToDivContent_WrapsInParagraphTag()
    {
        var result = FieldAppender.Append(
            "<div>existing</div>",
            "appended");

        result.ShouldBe("<div>existing</div><p>appended</p>");
    }

    // ── Append: asHtml parameter ──

    [Fact]
    public void Append_AsHtmlTrue_ForcesHtmlMode()
    {
        var result = FieldAppender.Append("plain existing", "new text", asHtml: true);

        result.ShouldBe("plain existing<p>new text</p>");
    }

    // ── LooksLikeHtml ──

    [Theory]
    [InlineData("<p>text</p>")]
    [InlineData("<div>content</div>")]
    [InlineData("<br>")]
    [InlineData("<br/>")]
    [InlineData("<br />")]
    [InlineData("<ul><li>item</li></ul>")]
    [InlineData("<h1>heading</h1>")]
    [InlineData("<h6>heading</h6>")]
    [InlineData("some text <em>emphasis</em> more text")]
    [InlineData("<strong>bold</strong>")]
    [InlineData("<span class=\"x\">text</span>")]
    [InlineData("<a href=\"url\">link</a>")]
    [InlineData("<img src=\"img.png\">")]
    [InlineData("<table><tr><td>cell</td></tr></table>")]
    [InlineData("<ol><li>ordered</li></ol>")]
    public void LooksLikeHtml_HtmlContent_ReturnsTrue(string value)
    {
        FieldAppender.LooksLikeHtml(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("no tags here")]
    [InlineData("just some words")]
    [InlineData("")]
    [InlineData("a < b > c")]
    [InlineData("less than < but no close")]
    [InlineData("greater than > but no open")]
    [InlineData("template<T> value")]
    [InlineData("x < 10 && y > 5")]
    public void LooksLikeHtml_PlainText_ReturnsFalse(string value)
    {
        FieldAppender.LooksLikeHtml(value).ShouldBeFalse();
    }
}
