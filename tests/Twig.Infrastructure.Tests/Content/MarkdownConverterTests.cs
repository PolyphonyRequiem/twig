using Microsoft.VisualStudio.TestTools.UnitTesting;
using Twig.Infrastructure.Content;

namespace Twig.Infrastructure.Tests.Content;

[TestClass]
public sealed class MarkdownConverterTests
{
    [TestMethod]
    public void ToHtml_Heading_ProducesH1Tag()
    {
        var result = MarkdownConverter.ToHtml("# Hello");

        StringAssert.Contains(result, "<h1");
        StringAssert.Contains(result, "Hello</h1>");
    }

    [TestMethod]
    public void ToHtml_BoldAndItalic_ProducesStrongAndEmTags()
    {
        var result = MarkdownConverter.ToHtml("**bold** _italic_");

        StringAssert.Contains(result, "<strong>bold</strong>");
        StringAssert.Contains(result, "<em>italic</em>");
    }

    [TestMethod]
    public void ToHtml_OrderedList_ProducesOlTags()
    {
        var result = MarkdownConverter.ToHtml("1. A\n2. B");

        StringAssert.Contains(result, "<ol>");
        StringAssert.Contains(result, "<li>A</li>");
    }

    [TestMethod]
    public void ToHtml_UnorderedList_ProducesUlTags()
    {
        var result = MarkdownConverter.ToHtml("- A\n- B");

        StringAssert.Contains(result, "<ul>");
        StringAssert.Contains(result, "<li>A</li>");
    }

    [TestMethod]
    public void ToHtml_GfmTable_ProducesTableTags()
    {
        const string table = """
            | Col1 | Col2 |
            |------|------|
            | A    | B    |
            """;

        var result = MarkdownConverter.ToHtml(table);

        StringAssert.Contains(result, "<table>");
        StringAssert.Contains(result, "<th>");
        StringAssert.Contains(result, "<td>");
    }

    [TestMethod]
    public void ToHtml_FencedCodeBlock_ProducesCodeTag()
    {
        const string code = """
            ```csharp
            var x = 1;
            ```
            """;

        var result = MarkdownConverter.ToHtml(code);

        StringAssert.Contains(result, "<code");
    }

    [TestMethod]
    public void ToHtml_TaskList_ProducesCheckedInput()
    {
        var result = MarkdownConverter.ToHtml("- [x] Done");

        StringAssert.Contains(result, "<input");
        StringAssert.Contains(result, "checked");
    }

    [TestMethod]
    public void ToHtml_NullInput_ReturnsEmpty()
    {
        var result = MarkdownConverter.ToHtml(null);

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void ToHtml_EmptyInput_ReturnsEmpty()
    {
        var result = MarkdownConverter.ToHtml("");

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void ToHtml_WhitespaceOnly_ReturnsEmpty()
    {
        var result = MarkdownConverter.ToHtml("   ");

        Assert.AreEqual(string.Empty, result);
    }
}
