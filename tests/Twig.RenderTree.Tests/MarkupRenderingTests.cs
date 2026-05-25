using System.IO;
using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class MarkupRenderingTests
{
    [Fact]
    public void StripMarkup_RemovesSimpleTags()
    {
        MarkupHelpers.StripMarkup("hello [red]world[/]").ShouldBe("hello world");
    }

    [Fact]
    public void StripMarkup_PreservesLiteralBracketEscapes()
    {
        MarkupHelpers.StripMarkup("[[done]] [green]ok[/]").ShouldBe("[done] ok");
    }

    [Fact]
    public void StripMarkup_HandlesUnterminatedTag()
    {
        MarkupHelpers.StripMarkup("plain [bad").ShouldBe("plain [bad");
    }

    [Fact]
    public void StripMarkup_EmptyStringRoundTrips()
    {
        MarkupHelpers.StripMarkup("").ShouldBe("");
    }

    [Fact]
    public void JsonRenderer_MarkupTopLevel_EmitsStrippedText()
    {
        var tree = new RenderTree(new[]
        {
            (RenderNode)new RenderNode.Markup("Set active item: #42 [[Foo]] [green]Active[/]"),
        });
        using var sw = new StringWriter();
        new JsonRenderer(sw).Render(tree);

        var json = sw.ToString();
        json.ShouldContain("\"text\"");
        json.ShouldContain("Set active item: #42 [Foo] Active");
        json.ShouldNotContain("[green]");
    }

    [Fact]
    public void JsonRenderer_MarkupInsideDocumentField_EmitsStrippedString()
    {
        var fields = new[]
        {
            new DocumentField("message", new RenderNode.Markup("[bold]Hello[/] world")),
        };
        var doc = new RenderNode.Document(null, fields);
        var tree = new RenderTree(new[] { (RenderNode)doc });

        using var sw = new StringWriter();
        new JsonRenderer(sw).Render(tree);

        var json = sw.ToString();
        json.ShouldContain("\"message\"");
        json.ShouldContain("\"Hello world\"");
    }

    [Fact]
    public void MinimalRenderer_Markup_StripsAndWritesLine()
    {
        var tree = new RenderTree(new[]
        {
            (RenderNode)new RenderNode.Markup("#42 [yellow]Doing[/]"),
        });
        using var sw = new StringWriter();
        new MinimalRenderer(sw).Render(tree);

        var lines = sw.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldBe("#42 Doing");
    }

    [Fact]
    public void IdsRenderer_Markup_EmitsNothing()
    {
        var tree = new RenderTree(new[]
        {
            (RenderNode)new RenderNode.Markup("#42 [yellow]Doing[/]"),
        });
        using var sw = new StringWriter();
        new IdsRenderer(sw).Render(tree);

        sw.ToString().ShouldBe(string.Empty);
    }
}
