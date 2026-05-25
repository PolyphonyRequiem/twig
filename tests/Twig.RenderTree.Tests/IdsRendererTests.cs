using System.Collections.Generic;
using System.IO;
using Shouldly;
using Xunit;

namespace Twig.RenderTree.Tests;

public sealed class IdsRendererTests
{
    [Fact]
    public void Record_WithIntegerIdField_EmitsId()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["title"] = RenderCell.String("Alpha"),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        SplitLines(writer).ShouldBe(["42"]);
    }

    [Fact]
    public void Record_WithoutIdField_EmitsNothing()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["title"] = RenderCell.String("Alpha"),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Record_IdFieldIsNonInteger_NotEmitted()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.String("not-an-integer"),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Table_EmitsOneIdPerRow()
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
                ["id"] = RenderCell.Integer(101),
                ["title"] = RenderCell.String("Alpha"),
            }),
            new RenderRow(null, new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(102),
                ["title"] = RenderCell.String("Beta"),
            }),
        };
        var tree = new RenderTree([new RenderNode.Table(null, columns, rows)]);

        renderer.Render(tree);

        SplitLines(writer).ShouldBe(["101", "102"]);
    }

    [Fact]
    public void TreeView_EmitsDepthFirstIds()
    {
        var (renderer, writer) = CreateRenderer();
        var grandchild = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(3),
            }),
            []);
        var child = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(2),
            }),
            [grandchild]);
        var root = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(1),
            }),
            [child]);
        var tree = new RenderTree([new RenderNode.TreeView(root)]);

        renderer.Render(tree);

        SplitLines(writer).ShouldBe(["1", "2", "3"]);
    }

    [Fact]
    public void NonIdKeys_AreIgnored()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["parentId"] = RenderCell.Integer(99),
            ["sourceId"] = RenderCell.Integer(98),
        };
        var tree = new RenderTree([new RenderNode.Record("link", fields)]);

        renderer.Render(tree);

        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Section_RecursesIntoChildren()
    {
        var (renderer, writer) = CreateRenderer();
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(7),
        };
        var tree = new RenderTree([
            new RenderNode.Section("group", [
                new RenderNode.Record("workItem", fields),
            ]),
        ]);

        renderer.Render(tree);

        SplitLines(writer).ShouldBe(["7"]);
    }

    [Fact]
    public void TextHintKeyValue_AreNotEligible()
    {
        var (renderer, writer) = CreateRenderer();
        var tree = new RenderTree([
            new RenderNode.Text("note"),
            new RenderNode.Hint("dim"),
            new RenderNode.KeyValue("id", RenderCell.Integer(1)),
        ]);

        renderer.Render(tree);

        // KeyValue has no row context; the renderer does not extract from it.
        writer.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void Record_WithNestedObjectIdField_EmitsBothIds()
    {
        var (renderer, writer) = CreateRenderer();
        var nested = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(99),
            ["other"] = RenderCell.String("ignored"),
        };
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["fields"] = new RenderCell(string.Empty, new RenderValue.Object(nested)),
        };
        var tree = new RenderTree([new RenderNode.Record("workItem", fields)]);

        renderer.Render(tree);

        // Outer id first, then the nested object's id.
        SplitLines(writer).ShouldBe(["42", "99"]);
    }

    private static (IdsRenderer Renderer, StringWriter Writer) CreateRenderer()
    {
        var writer = new StringWriter();
        return (new IdsRenderer(writer), writer);
    }

    private static string[] SplitLines(StringWriter writer)
    {
        return writer.ToString().Split([System.Environment.NewLine], System.StringSplitOptions.RemoveEmptyEntries);
    }
}
