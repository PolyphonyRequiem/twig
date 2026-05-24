using Shouldly;
using Twig.RenderTree;
using Xunit;

namespace Twig.RenderTree.Tests;

/// <summary>
/// Golden fixtures: representative render-tree shapes for three real twig outputs.
/// These pin down that the chosen RenderNode vocabulary can express the existing
/// formatter cases without distortion. No renderer is exercised — only the data
/// shape is asserted. As subsequent slices wire renderers in, these fixtures
/// double as the input side of golden render snapshots.
/// </summary>
public sealed class RenderTreeFixturesTests
{
    /// <summary>
    /// Shape 1 — single structured object. Mirrors <c>twig set #42</c> /
    /// <c>FormatSetConfirmation</c>: a one-object response with id, title, state, type.
    /// </summary>
    [Fact]
    public void SetConfirmation_projects_as_Record_node()
    {
        var tree = new RenderTree(new RenderNode[]
        {
            new RenderNode.Record("workItem", new Dictionary<string, RenderCell>
            {
                ["id"] = RenderCell.Integer(42, "#42"),
                ["title"] = RenderCell.String("Fix login bug"),
                ["state"] = RenderCell.String("Active", Severity.Success),
                ["type"] = RenderCell.String("Bug"),
            }),
        });

        tree.Nodes.Count.ShouldBe(1);
        var record = tree.Nodes[0].ShouldBeOfType<RenderNode.Record>();
        record.Kind.ShouldBe("workItem");
        record.Fields.Keys.ShouldBe(new[] { "id", "title", "state", "type" }, ignoreOrder: true);

        // Display vs machine value split — JSON renderer will emit 42, human renderer "#42".
        var id = record.Fields["id"];
        id.DisplayText.ShouldBe("#42");
        ((RenderValue.Integer)id.Value).Value.ShouldBe(42L);

        // Semantic severity carried separately from styling.
        record.Fields["state"].Severity.ShouldBe(Severity.Success);
    }

    /// <summary>
    /// Shape 2 — homogeneous list of rows. Mirrors <c>FormatWorkItemLinks</c>:
    /// a list of <c>source -&gt; target</c> link records with a stable shape.
    /// </summary>
    [Fact]
    public void WorkItemLinks_projects_as_Table_node()
    {
        var columns = new[]
        {
            new RenderColumn("sourceId", "Source"),
            new RenderColumn("linkType", "Link"),
            new RenderColumn("targetId", "Target"),
        };
        var rows = new[]
        {
            Link(1, "Parent", 100),
            Link(2, "Related", 101),
        };
        var tree = new RenderTree(new RenderNode[]
        {
            new RenderNode.Table(Caption: "Links", columns, rows),
        });

        var table = tree.Nodes[0].ShouldBeOfType<RenderNode.Table>();
        table.Caption.ShouldBe("Links");
        table.Columns.Select(c => c.Key).ShouldBe(new[] { "sourceId", "linkType", "targetId" });
        table.Rows.Count.ShouldBe(2);

        // Row cell keys align with column keys (renderer relies on this).
        foreach (var row in table.Rows)
            row.Cells.Keys.ShouldBe(table.Columns.Select(c => c.Key), ignoreOrder: true);

        // First row machine values are typed, not string-coerced.
        var first = table.Rows[0];
        ((RenderValue.Integer)first.Cells["sourceId"].Value).Value.ShouldBe(1L);
        ((RenderValue.String)first.Cells["linkType"].Value).Value.ShouldBe("Parent");
        ((RenderValue.Integer)first.Cells["targetId"].Value).Value.ShouldBe(100L);

        static RenderRow Link(int source, string type, int target) => new(
            "workItemLink",
            new Dictionary<string, RenderCell>
            {
                ["sourceId"] = RenderCell.Integer(source, $"#{source}"),
                ["linkType"] = RenderCell.String(type),
                ["targetId"] = RenderCell.Integer(target, $"#{target}"),
            });
    }

    /// <summary>
    /// Shape 3 — hierarchy. Mirrors <c>FormatTree</c>: parent chain + focused item
    /// + children rendered as nested branches. Each branch carries a row payload so
    /// renderers can format the line (human draws box-drawing, JSON nests arrays,
    /// minimal flattens depth-first).
    /// </summary>
    [Fact]
    public void WorkTree_projects_as_TreeView_node()
    {
        var grandchild = Branch(id: 300, title: "Grandchild task", state: "New");
        var child = Branch(id: 200, title: "Child story", state: "Active", children: new[] { grandchild });
        var root = Branch(id: 100, title: "Root epic", state: "Active", children: new[] { child });

        var tree = new RenderTree(new RenderNode[]
        {
            new RenderNode.TreeView(root),
        });

        var tv = tree.Nodes[0].ShouldBeOfType<RenderNode.TreeView>();
        ((RenderValue.Integer)tv.Root.Row.Cells["id"].Value).Value.ShouldBe(100L);

        // Hierarchy is preserved exactly — renderers pick how to draw it.
        tv.Root.Children.Count.ShouldBe(1);
        var c = tv.Root.Children[0];
        ((RenderValue.Integer)c.Row.Cells["id"].Value).Value.ShouldBe(200L);

        c.Children.Count.ShouldBe(1);
        var gc = c.Children[0];
        ((RenderValue.Integer)gc.Row.Cells["id"].Value).Value.ShouldBe(300L);
        gc.Children.Count.ShouldBe(0);

        static RenderTreeBranch Branch(int id, string title, string state, IReadOnlyList<RenderTreeBranch>? children = null)
            => new(
                new RenderRow("workItem", new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.Integer(id, $"#{id}"),
                    ["title"] = RenderCell.String(title),
                    ["state"] = RenderCell.String(state),
                }),
                children ?? Array.Empty<RenderTreeBranch>());
    }

    /// <summary>
    /// Composite shape — a workspace view mixing a heading section with a record
    /// for context and a table for sprint items. Mirrors the rough shape of
    /// <c>twig workspace</c> output, proving sections compose with other node kinds.
    /// </summary>
    [Fact]
    public void WorkspaceLike_view_composes_Section_Record_and_Table()
    {
        var tree = new RenderTree(new RenderNode[]
        {
            new RenderNode.Section("Context", new RenderNode[]
            {
                new RenderNode.Record("workItem", new Dictionary<string, RenderCell>
                {
                    ["id"] = RenderCell.Integer(42, "#42"),
                    ["title"] = RenderCell.String("Active task"),
                }),
            }),
            new RenderNode.Section("Sprint", new RenderNode[]
            {
                new RenderNode.Table(
                    Caption: null,
                    Columns: new[]
                    {
                        new RenderColumn("id", "ID"),
                        new RenderColumn("title", "Title"),
                    },
                    Rows: new[]
                    {
                        new RenderRow("workItem", new Dictionary<string, RenderCell>
                        {
                            ["id"] = RenderCell.Integer(1, "#1"),
                            ["title"] = RenderCell.String("First"),
                        }),
                    }),
            }),
            new RenderNode.Hint("Use twig set <id> to focus an item"),
        });

        tree.Nodes.Count.ShouldBe(3);
        tree.Nodes[0].ShouldBeOfType<RenderNode.Section>().Header.ShouldBe("Context");
        tree.Nodes[1].ShouldBeOfType<RenderNode.Section>().Header.ShouldBe("Sprint");
        tree.Nodes[2].ShouldBeOfType<RenderNode.Hint>();
    }
}
