using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>
/// Renders a <see cref="RenderTree.RenderTree"/> to an <see cref="IAnsiConsole"/>
/// using Spectre.Console primitives. This is the human-format renderer in the
/// AB#3301 seam.
/// </summary>
/// <remarks>
/// <para>
/// The renderer walks the <see cref="RenderNode"/> vocabulary and emits Spectre
/// markup: <c>Text</c>/<c>Hint</c> become coloured markup lines, <c>KeyValue</c>
/// becomes a labelled line, <c>Record</c> becomes a key-value listing, <c>Table</c>
/// becomes a Spectre <c>Table</c>, <c>TreeView</c> becomes a Spectre <c>Tree</c>,
/// and <c>Section</c> becomes a bold header followed by indented children.
/// </para>
/// <para>
/// The renderer is intentionally generic — it does not understand work item
/// types, state categories, or twig-specific theming. Command code feeds it
/// <see cref="Severity"/>-tagged cells and the renderer maps severity to colour.
/// Theme-aware presentation (type badges, state colours, icon modes) stays in
/// <see cref="SpectreTheme"/> and the commands that depend on it; commands
/// project pre-themed display strings into <see cref="RenderCell.DisplayText"/>
/// before handing the tree to this renderer.
/// </para>
/// <para>
/// Named <c>SpectreNodeRenderer</c> rather than <c>SpectreRenderer</c> to coexist
/// with the legacy <see cref="SpectreRenderer"/> during the staged collapse of
/// <c>IOutputFormatter</c> in AB#3301. The legacy renderer is retired in the
/// final slice; this type is then renamed.
/// </para>
/// </remarks>
internal sealed class SpectreNodeRenderer(IAnsiConsole console) : IRenderer
{
    public void Render(RenderTree.RenderTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        foreach (var node in tree.Nodes)
        {
            this.WriteNode(node);
        }
    }

    private void WriteNode(RenderNode node)
    {
        switch (node)
        {
            case RenderNode.Text text:
                this.WriteText(text);
                break;
            case RenderNode.Hint hint:
                console.MarkupLine($"[grey]{Markup.Escape(hint.Content)}[/]");
                break;
            case RenderNode.KeyValue kv:
                this.WriteKeyValue(kv);
                break;
            case RenderNode.Record rec:
                this.WriteRecord(rec);
                break;
            case RenderNode.Table table:
                this.WriteTable(table);
                break;
            case RenderNode.TreeView treeView:
                this.WriteTreeView(treeView);
                break;
            case RenderNode.Section section:
                this.WriteSection(section);
                break;
        }
    }

    private void WriteText(RenderNode.Text text)
    {
        var escaped = Markup.Escape(text.Content);
        var color = MarkupColorForSeverity(text.Severity);
        if (color is null)
        {
            console.WriteLine(text.Content);
        }
        else
        {
            console.MarkupLine($"[{color}]{escaped}[/]");
        }
    }

    private void WriteKeyValue(RenderNode.KeyValue kv)
    {
        var key = Markup.Escape(kv.Key);
        var valueMarkup = FormatCellMarkup(kv.Value);
        var severity = kv.Severity != Severity.None ? kv.Severity : kv.Value.Severity;
        var color = MarkupColorForSeverity(severity);
        var wrappedValue = color is null ? valueMarkup : $"[{color}]{valueMarkup}[/]";
        console.MarkupLine($"[bold]{key}[/]: {wrappedValue}");
    }

    private void WriteRecord(RenderNode.Record rec)
    {
        if (!string.IsNullOrEmpty(rec.Kind))
        {
            console.MarkupLine($"[bold]{Markup.Escape(rec.Kind)}[/]");
        }

        foreach (var (key, cell) in rec.Fields)
        {
            var valueMarkup = FormatCellMarkup(cell);
            var color = MarkupColorForSeverity(cell.Severity);
            var wrapped = color is null ? valueMarkup : $"[{color}]{valueMarkup}[/]";
            console.MarkupLine($"  [bold]{Markup.Escape(key)}[/]: {wrapped}");
        }
    }

    private void WriteTable(RenderNode.Table table)
    {
        if (!string.IsNullOrEmpty(table.Caption))
        {
            console.MarkupLine($"[bold]{Markup.Escape(table.Caption)}[/]");
        }

        var spectreTable = new Table();
        foreach (var column in table.Columns)
        {
            spectreTable.AddColumn(new TableColumn($"[bold]{Markup.Escape(column.DisplayName)}[/]"));
        }

        foreach (var row in table.Rows)
        {
            var cells = new IRenderable[table.Columns.Count];
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var key = table.Columns[i].Key;
                if (row.Cells.TryGetValue(key, out var cell))
                {
                    var markup = FormatCellMarkup(cell);
                    var color = MarkupColorForSeverity(cell.Severity);
                    cells[i] = new Markup(color is null ? markup : $"[{color}]{markup}[/]");
                }
                else
                {
                    cells[i] = new Markup(string.Empty);
                }
            }

            spectreTable.AddRow(cells);
        }

        console.Write(spectreTable);
    }

    private void WriteTreeView(RenderNode.TreeView treeView)
    {
        var rootLabel = FormatRowMarkup(treeView.Root.Row);
        var spectreTree = new Tree(rootLabel);
        foreach (var child in treeView.Root.Children)
        {
            AppendBranch(spectreTree, child);
        }

        console.Write(spectreTree);
    }

    private static void AppendBranch(IHasTreeNodes parent, RenderTreeBranch branch)
    {
        var label = FormatRowMarkup(branch.Row);
        var node = parent.AddNode(label);
        foreach (var child in branch.Children)
        {
            AppendBranch(node, child);
        }
    }

    private void WriteSection(RenderNode.Section section)
    {
        if (!string.IsNullOrEmpty(section.Header))
        {
            console.MarkupLine($"[bold underline]{Markup.Escape(section.Header)}[/]");
        }

        foreach (var child in section.Children)
        {
            this.WriteNode(child);
        }
    }

    private static string FormatRowMarkup(RenderRow row)
    {
        if (row.Cells.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(row.Cells.Count);
        foreach (var (_, cell) in row.Cells)
        {
            var markup = FormatCellMarkup(cell);
            var color = MarkupColorForSeverity(cell.Severity);
            parts.Add(color is null ? markup : $"[{color}]{markup}[/]");
        }

        return string.Join("  ", parts);
    }

    private static string FormatCellMarkup(RenderCell cell)
    {
        return Markup.Escape(cell.DisplayText);
    }

    private static string? MarkupColorForSeverity(Severity severity) => severity switch
    {
        Severity.Info => "blue",
        Severity.Success => "green",
        Severity.Warning => "yellow",
        Severity.Error => "red",
        _ => null,
    };
}
