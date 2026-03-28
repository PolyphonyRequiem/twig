using Shouldly;
using Twig.Infrastructure.Content;
using Xunit;

namespace Twig.Infrastructure.Tests.Content;

/// <summary>
/// Tests for <see cref="MarkdownConverter"/>: verifies Markdig pipeline
/// produces expected HTML for common Markdown constructs and handles edge cases.
/// </summary>
public class MarkdownConverterTests
{
    [Fact]
    public void ToHtml_Heading_ProducesH1Tag()
    {
        var result = MarkdownConverter.ToHtml("# Hello");

        result.ShouldContain("<h1");
        result.ShouldContain("Hello</h1>");
    }

    [Fact]
    public void ToHtml_BoldAndItalic_ProducesStrongAndEmTags()
    {
        var result = MarkdownConverter.ToHtml("**bold** _italic_");

        result.ShouldContain("<strong>bold</strong>");
        result.ShouldContain("<em>italic</em>");
    }

    [Fact]
    public void ToHtml_OrderedList_ProducesOlTags()
    {
        var result = MarkdownConverter.ToHtml("1. A\n2. B");

        result.ShouldContain("<ol>");
        result.ShouldContain("<li>A</li>");
    }

    [Fact]
    public void ToHtml_UnorderedList_ProducesUlTags()
    {
        var result = MarkdownConverter.ToHtml("- A\n- B");

        result.ShouldContain("<ul>");
        result.ShouldContain("<li>A</li>");
    }

    [Fact]
    public void ToHtml_GfmTable_ProducesTableTags()
    {
        const string table = """
            | Col1 | Col2 |
            |------|------|
            | A    | B    |
            """;

        var result = MarkdownConverter.ToHtml(table);

        result.ShouldContain("<table>");
        result.ShouldContain("<th>Col1</th>");
        result.ShouldContain("<td>A</td>");
    }

    [Fact]
    public void ToHtml_FencedCodeBlock_ProducesCodeTag()
    {
        const string code = """
            ```csharp
            var x = 1;
            ```
            """;

        var result = MarkdownConverter.ToHtml(code);

        result.ShouldContain("<code");
    }

    [Fact]
    public void ToHtml_TaskList_ProducesCheckedInput()
    {
        var result = MarkdownConverter.ToHtml("- [x] Done");

        result.ShouldContain("<input");
        result.ShouldContain("checked");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ToHtml_NullOrWhitespace_ReturnsEmpty(string? input)
    {
        var result = MarkdownConverter.ToHtml(input);

        result.ShouldBe(string.Empty);
    }
}
