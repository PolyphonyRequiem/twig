using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

/// <summary>
/// Constructor + structural tests for the <see cref="RenderNode"/> DU variants.
/// </summary>
public sealed class RenderNodeTests
{
    [Fact]
    public void Text_carries_content_and_defaults_severity_to_None()
    {
        var n = new RenderNode.Text("hello");
        n.Content.ShouldBe("hello");
        n.Severity.ShouldBe(Severity.None);
    }

    [Fact]
    public void Hint_carries_content()
    {
        new RenderNode.Hint("try --help").Content.ShouldBe("try --help");
    }

    [Fact]
    public void KeyValue_carries_key_value_and_severity()
    {
        var kv = new RenderNode.KeyValue("state", RenderCell.String("Active"), Severity.Success);
        kv.Key.ShouldBe("state");
        kv.Severity.ShouldBe(Severity.Success);
        ((RenderValue.String)kv.Value.Value).Value.ShouldBe("Active");
    }

    [Fact]
    public void Record_carries_kind_and_fields()
    {
        var fields = new Dictionary<string, RenderCell>
        {
            ["id"] = RenderCell.Integer(42),
            ["title"] = RenderCell.String("Fix bug"),
        };
        var r = new RenderNode.Record("workItem", fields);
        r.Kind.ShouldBe("workItem");
        r.Fields.Count.ShouldBe(2);
        ((RenderValue.Integer)r.Fields["id"].Value).Value.ShouldBe(42L);
    }

    [Fact]
    public void Table_carries_columns_and_rows()
    {
        var columns = new[]
        {
            new RenderColumn("id", "ID"),
            new RenderColumn("title", "Title"),
        };
        var rows = new[]
        {
            new RenderRow("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(1),
                ["title"] = RenderCell.String("first"),
            }),
        };
        var t = new RenderNode.Table("Sprint", columns, rows);
        t.Caption.ShouldBe("Sprint");
        t.Columns.Count.ShouldBe(2);
        t.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public void TreeView_carries_root_branch()
    {
        var leaf = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell> { ["id"] = RenderCell.Integer(3) }),
            Array.Empty<RenderTreeBranch>());
        var root = new RenderTreeBranch(
            new RenderRow("workItem", new Dictionary<string, RenderCell> { ["id"] = RenderCell.Integer(1) }),
            new[] { leaf });
        var tv = new RenderNode.TreeView(root);

        tv.Root.Children.Count.ShouldBe(1);
        ((RenderValue.Integer)tv.Root.Children[0].Row.Cells["id"].Value).Value.ShouldBe(3L);
    }

    [Fact]
    public void Section_carries_header_and_children()
    {
        var s = new RenderNode.Section("Sprint", new RenderNode[] { new RenderNode.Text("hello") });
        s.Header.ShouldBe("Sprint");
        s.Children.Count.ShouldBe(1);
    }

    [Fact]
    public void RenderNode_pattern_matches_exhaustively()
    {
        // Compile-time proof that all variants are reachable. Renderers will write
        // switch expressions like this — one branch per node kind.
        static string Tag(RenderNode n) => n switch
        {
            RenderNode.Text => "text",
            RenderNode.Hint => "hint",
            RenderNode.KeyValue => "kv",
            RenderNode.Record => "record",
            RenderNode.Table => "table",
            RenderNode.TreeView => "tree",
            RenderNode.Section => "section",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Tag(new RenderNode.Text("x")).ShouldBe("text");
        Tag(new RenderNode.Hint("x")).ShouldBe("hint");
        Tag(new RenderNode.KeyValue("k", RenderCell.String("v"))).ShouldBe("kv");
        Tag(new RenderNode.Record(null, new Dictionary<string, RenderCell>())).ShouldBe("record");
        Tag(new RenderNode.Table(null, Array.Empty<RenderColumn>(), Array.Empty<RenderRow>())).ShouldBe("table");
        Tag(new RenderNode.TreeView(new RenderTreeBranch(new RenderRow(null, new Dictionary<string, RenderCell>()), Array.Empty<RenderTreeBranch>()))).ShouldBe("tree");
        Tag(new RenderNode.Section(null, Array.Empty<RenderNode>())).ShouldBe("section");
    }

    [Fact]
    public void RenderTree_carries_nodes()
    {
        var tree = new RenderTree(new RenderNode[]
        {
            new RenderNode.Text("welcome"),
            new RenderNode.Hint("type twig --help"),
        });
        tree.Nodes.Count.ShouldBe(2);
    }
}
