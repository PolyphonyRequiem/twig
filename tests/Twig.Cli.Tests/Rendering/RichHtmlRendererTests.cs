using System.Text.RegularExpressions;
using Shouldly;
using Twig.Formatters;
using Twig.Rendering;
using Twig.RenderTree;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public sealed class RichHtmlRendererTests
{
    private static string Render(string html, int width = 80, bool ansi = false) =>
        new RendererFactory().CaptureHuman(new RenderTree.RenderTree(RichHtmlRenderer.Render(html, width)),
            width, ansi ? "always" : "never").Text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void SourceControlsEntitiesAndMarkupCannotCreateTerminalCommands()
    {
        var output = Render("<p>[red]literal[/] \u001b[2J \u001b]52;c;secret\a &#27;[31m &#x85; &lt;b&gt;</p>" +
            "<script>script-secret</script><style>style-secret</style><img src='https://example.invalid/image'>" +
            "<a href='javascript:alert(1)'>unsafe-link</a>", ansi: true);
        var withoutRendererStyle = Regex.Replace(output, "\\x1b\\[[0-9;:]*m", "");
        withoutRendererStyle.ShouldNotContain("\u001b");
        withoutRendererStyle.ShouldNotContain("\a");
        withoutRendererStyle.ShouldNotContain("\u0085");
        withoutRendererStyle.ShouldContain("[red]literal[/]");
        withoutRendererStyle.ShouldContain("\\u001b[2J");
        withoutRendererStyle.ShouldContain("<b>");
        withoutRendererStyle.ShouldNotContain("script-secret");
        withoutRendererStyle.ShouldNotContain("style-secret");
        withoutRendererStyle.ShouldNotContain("javascript:");
        withoutRendererStyle.ShouldNotContain("example.invalid/image");
    }

    [Fact]
    public void StructuredContentRetainsCodeIndentationNestedListsLinksAndTableTail()
    {
        var output = Render("<html><body><h2>Plan</h2><p>Paragraph one</p><p>Paragraph two</p>" +
            "<ol><li>First<ul><li>Nested</li></ul></li><li>Second</li></ol>" +
            "<pre><code>    if (ready) {\n        finish();\n    }</code></pre>" +
            "<a href='https://example.test/a?q=one&amp;v=two'>Read more</a>" +
            "<table><tr><th>Key</th><th>Value</th></tr><tr><td>Long</td><td>" + new string('x', 200) +
            " FINAL-CELL</td></tr></table></body></html>", width: 64);
        output.ShouldContain("Plan");
        output.ShouldContain("Paragraph one");
        output.ShouldContain("Paragraph two");
        output.ShouldContain("1. First");
        output.ShouldContain("• Nested");
        output.ShouldContain("2. Second");
        output.ShouldContain("    if (ready)");
        output.ShouldContain("        finish();");
        output.ShouldContain("https://example.test/a?q=one&v=two");
        output.ShouldContain("FINAL-CELL");
        var first = output.IndexOf("Paragraph one", StringComparison.Ordinal);
        var second = output.IndexOf("Paragraph two", StringComparison.Ordinal);
        output[first..second].ShouldContain("\n\n");
    }

    [Fact]
    public void NarrowTablesStackEveryColumnRatherThanDroppingValues()
    {
        var output = Render("<table><tr><th>One</th><th>Two</th><th>Three</th></tr>" +
            "<tr><td>alpha</td><td>beta</td><td>omega</td></tr></table>", width: 12);
        output.ShouldContain("One: alpha");
        output.ShouldContain("Two: beta");
        output.ShouldContain("omega");
    }

    [Fact]
    public void MalformedTagsCannotBreakRenderingOrSwallowSubsequentListAndCells()
    {
        var output = Render("text</b><b>bold<i>both</b> tail</i>" +
            "<ul><li>first<li>last</ul><table><tr><th>Head<td>value</tr><tr><td>tail</table>");
        output.ShouldContain("text");
        output.ShouldContain("boldboth");
        output.ShouldContain("tail");
        output.ShouldContain("first");
        output.ShouldContain("last");
        output.ShouldContain("value");
    }

    [Fact]
    public void PreviewCapRemainsSafeAcrossMultilineEmphasisWhileFullDetailKeepsTail()
    {
        var html = "<b>" + string.Join("<br>", Enumerable.Range(1, 40).Select(i => $"line-{i:D2}")) + "</b>";
        var preview = FormatterHelpers.HtmlToSpectreMarkup(html);
        var previewText = new RendererFactory().CaptureHuman(new RenderTree.RenderTree([new RenderNode.Markup(preview)]), 80, "never").Text;
        previewText.ShouldContain("line-30");
        previewText.ShouldNotContain("line-31");
        previewText.ShouldContain("more lines");
        Render(html).ShouldContain("line-40");
    }
}
