using System.Collections.Generic;
using System.IO;
using Shouldly;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class MinimalRendererTests
{
    [Fact]
    public void Text_WritesContentLine()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([new RenderNode.Text("hello world")]);

        renderer.Render(tree);

        writer.ToString().ShouldBe("hello world" + System.Environment.NewLine);
    }

    [Fact]
    public void Hint_IsSuppressed()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([new RenderNode.Hint("try --help")]);

        renderer.Render(tree);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void KeyValue_WritesEqualsLine()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.KeyValue("state", RenderCell.String("Active")),
        ]);

        renderer.Render(tree);

        writer.ToString().Trim().ShouldBe("state=Active");
    }

    [Fact]
    public void Record_KindEmittedFirst_ThenFields()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["title"] = RenderCell.String("Alpha"),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        var lines = SplitLines(writer);
        lines.ShouldBe(["kind=workItem", "id=42", "title=Alpha"]);
    }

    [Fact]
    public void Table_HeaderThenTabSeparatedRows()
    {
        var (renderer, writer) = CreateRenderer();
        var columns = new[]
        {
            new RenderColumn("id", "ID"),
            new RenderColumn("title", "Title"),
        };
        var rows = new[]
        {
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(1),
                ["title"] = RenderCell.String("Alpha"),
            }),
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(2),
                ["title"] = RenderCell.String("Beta"),
            }),
        };
        var tree = new RenderTree([new RenderNode.Table(null, columns, rows)]);

        renderer.Render(tree);

        var lines = SplitLines(writer);
        lines.ShouldBe(["ID\tTitle", "1\tAlpha", "2\tBeta"]);
    }

    [Fact]
    public void TreeView_DepthFirstWithIndent()
    {
        var (renderer, writer) = CreateRenderer();
        var grandchild = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["title"] = RenderCell.String("Grand"),
            }),
            []);
        var child = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["title"] = RenderCell.String("Child"),
            }),
            [grandchild]);
        var root = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["title"] = RenderCell.String("Root"),
            }),
            [child]);
        var tree = new RenderTree([new RenderNode.TreeView(root)]);

        renderer.Render(tree);

        var lines = SplitLines(writer);
        lines.ShouldBe(["Root", "  Child", "    Grand"]);
    }

    [Fact]
    public void Section_HeaderThenChildren()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Section("Pending", [
                new RenderNode.Text("first"),
                new RenderNode.Text("second"),
            ]),
        ]);

        renderer.Render(tree);

        var lines = SplitLines(writer);
        lines.ShouldBe(["Pending", "first", "second"]);
    }

    [Fact]
    public void EmptyTree_ProducesNoOutput()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([]);

        renderer.Render(tree);

        writer.ToString().ShouldBeEmpty();
    }

    private static (MinimalRenderer Renderer, StringWriter Writer) CreateRenderer()
    {
        var writer = new StringWriter();
        return (new MinimalRenderer(writer), writer);
    }

    private static string[] SplitLines(StringWriter writer)
    {
        return writer.ToString().Split([System.Environment.NewLine], System.StringSplitOptions.RemoveEmptyEntries);
    }
}
