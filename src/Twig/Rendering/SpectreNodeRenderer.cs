using Spectre.Console;
using Spectre.Console.Rendering;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>
/// Renders a <see cref="RenderTree.RenderTree"/> to an <see cref="IAnsiConsole"/>
/// using Spectre.Console primitives. This is the human-format renderer in the
/// AB#3301 seam.
/// </summary>
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
            case RenderNode.Markup markup:
                console.MarkupLine(markup.Content);
                break;
            case RenderNode.Hint hint:
                // Match legacy HumanOutputFormatter glyph + "hint:" prefix so commands
                // migrated to RenderNode.Hint preserve UX parity.
                console.MarkupLine($"[yellow]→[/] [grey]hint: {Markup.Escape(hint.Content)}[/]");
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
            case RenderNode.FieldBlock block:
                console.Write(new FieldBlockContent(block, console.Profile.Capabilities.Unicode));
                break;
            case RenderNode.TreeView treeView:
                this.WriteTreeView(treeView);
                break;
            case RenderNode.Section section:
                this.WriteSection(section);
                break;
            case RenderNode.Document doc:
                this.WriteDocument(doc);
                break;
        }
    }

    private void WriteText(RenderNode.Text text)
    {
        var escaped = Markup.Escape(text.Content);
        var (color, prefix) = SeverityDecoration(text.Severity);
        if (color is null)
        {
            // No severity → write raw content (preserves whitespace, no markup escaping needed).
            console.WriteLine(text.Content);
            return;
        }

        // Render Spectre-coloured icon prefix (matches legacy HumanOutputFormatter
        // glyphs: ✓ / ✗ / ! / →) followed by the escaped content. The prefix glyph
        // colour matches the severity colour so the icon and message read as one.
        if (prefix is null)
        {
            console.MarkupLine($"[{color}]{escaped}[/]");
        }
        else
        {
            console.MarkupLine($"[{color}]{prefix}[/] {escaped}");
        }
    }

    private static (string? Color, string? Prefix) SeverityDecoration(Severity severity) => severity switch
    {
        Severity.Success => ("green", "✓"),
        Severity.Error => ("red", "✗"),
        Severity.Warning => ("yellow", "!"),
        Severity.Info => ("blue", null),
        // Muted dims the line but adds no glyph — it recedes rather than announcing itself.
        Severity.Muted => ("grey", null),
        _ => (null, null),
    };

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
        if (HasBody(treeView.Root))
        {
            WriteCompositeBranch(treeView.Root, "", true, true);
            return;
        }
        var rootLabel = FormatRowMarkup(treeView.Root.Row);
        var spectreTree = new Tree(rootLabel);
        foreach (var child in treeView.Root.Children)
        {
            AppendBranch(spectreTree, child);
        }

        console.Write(spectreTree);
    }

    private static bool HasBody(RenderTreeBranch branch) => branch.Body.Count > 0 || branch.Children.Any(HasBody);

    private void WriteCompositeBranch(RenderTreeBranch branch, string ancestor, bool last, bool root)
    {
        var unicode = console.Profile.Capabilities.Unicode;
        var bar = unicode ? "│  " : "|  ";
        var tee = unicode ? "├─ " : "+- ";
        var elbow = unicode ? "└─ " : "\\- ";
        var continuation = root ? "" : ancestor + (last ? "   " : bar);
        var first = root ? "" : ancestor + (last ? elbow : tee);
        console.Write(new GuidedContent(new Markup(FormatRowMarkup(branch.Row)), first, continuation));
        var bodyGuide = continuation + (branch.Children.Count > 0 ? bar : "   ");
        foreach (var node in branch.Body)
        {
            IRenderable content = node switch
            {
                RenderNode.FieldBlock block => new FieldBlockContent(block, unicode),
                RenderNode.Text text => new Text(text.Content),
                RenderNode.Hint hint => new Text(hint.Content),
                _ => throw new NotSupportedException($"Unsupported composite body node {node.GetType().Name}"),
            };
            console.Write(new GuidedContent(content, bodyGuide, bodyGuide));
        }
        // Spacing is part of the node, so parent/sibling guides never disappear into a gap.
        console.Write(new GuidedContent(new Text(" "), bodyGuide, bodyGuide));
        for (var i = 0; i < branch.Children.Count; i++)
            WriteCompositeBranch(branch.Children[i], continuation, i == branch.Children.Count - 1, false);
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

    private void WriteDocument(RenderNode.Document doc)
    {
        foreach (var field in doc.Fields)
        {
            if (field.Audience == RenderAudience.MachineOnly)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(field.Header))
            {
                console.MarkupLine($"[bold]{Markup.Escape(field.Header)}[/]");
            }

            var displayNode = field.HumanOverride ?? field.Node;
            this.WriteNode(displayNode);
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
            // Cells that exist purely for machine consumers carry an empty
            // DisplayText (see RenderCell.DisplayText). Emitting them would put
            // stray separators and bare "true"/id values into human rows.
            if (cell.DisplayText.Length == 0)
            {
                continue;
            }

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
        Severity.Muted => "grey",
        _ => null,
    };
}
