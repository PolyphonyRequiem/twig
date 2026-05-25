using System.Globalization;
using System.IO;

namespace Twig.RenderTree;

/// <summary>
/// Renders a <see cref="RenderTree"/> as line-oriented plain text suitable for
/// scripting and piping (no ANSI, no markup, no decoration beyond newlines and
/// tabs).
/// </summary>
/// <remarks>
/// <para>
/// The output shape is intentionally generic and tree-driven, not command-aware:
/// </para>
/// <list type="bullet">
/// <item><see cref="RenderNode.Text"/> — one line with the raw content.</item>
/// <item><see cref="RenderNode.Hint"/> — suppressed (hints are human-only).</item>
/// <item><see cref="RenderNode.KeyValue"/> — one line as <c>key=value</c>.</item>
/// <item><see cref="RenderNode.Record"/> — one <c>key=value</c> line per field.
/// When <see cref="RenderNode.Record.Kind"/> is set, a <c>kind</c> field is prepended.</item>
/// <item><see cref="RenderNode.Table"/> — tab-separated header row followed by
/// tab-separated cell rows (one row per line).</item>
/// <item><see cref="RenderNode.TreeView"/> — depth-first traversal; each row is
/// rendered as space-separated cell display text, indented two spaces per depth.</item>
/// <item><see cref="RenderNode.Section"/> — optional header line (key preferred,
/// human header fallback) followed by recursively-rendered children.</item>
/// </list>
/// <para>
/// Commands needing the historical "CTX/SPR/SEED" prefix style of
/// <c>MinimalOutputFormatter</c> can encode the prefix via
/// <see cref="RenderRow.Kind"/> or <see cref="RenderNode.Section.Header"/> and
/// rely on the generic projection; slice-by-slice migration in AB#3301 wires
/// each command's tree to this contract.
/// </para>
/// </remarks>
public sealed class MinimalRenderer(TextWriter output) : IRenderer
{
    public void Render(RenderTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        foreach (var node in tree.Nodes)
        {
            this.WriteNode(node, depth: 0);
        }
    }

    private void WriteNode(RenderNode node, int depth)
    {
        switch (node)
        {
            case RenderNode.Text text:
                output.WriteLine(text.Content);
                break;
            case RenderNode.Hint:
                // Hints are dim human guidance — suppressed in machine output.
                break;
            case RenderNode.KeyValue kv:
                output.WriteLine($"{kv.Key}={kv.Value.DisplayText}");
                break;
            case RenderNode.Record rec:
                this.WriteRecord(rec);
                break;
            case RenderNode.Table table:
                this.WriteTable(table);
                break;
            case RenderNode.TreeView treeView:
                WriteBranch(treeView.Root, depth);
                break;
            case RenderNode.Section section:
                this.WriteSection(section, depth);
                break;
            case RenderNode.Document doc:
                this.WriteDocument(doc, depth);
                break;
        }
    }

    private void WriteRecord(RenderNode.Record rec)
    {
        if (!string.IsNullOrEmpty(rec.Kind))
        {
            output.WriteLine($"kind={rec.Kind}");
        }

        foreach (var (key, cell) in rec.Fields)
        {
            output.WriteLine($"{key}={cell.DisplayText}");
        }
    }

    private void WriteTable(RenderNode.Table table)
    {
        if (table.Columns.Count == 0)
        {
            return;
        }

        var header = string.Join("\t", table.Columns.Select(c => c.DisplayName));
        output.WriteLine(header);

        foreach (var row in table.Rows)
        {
            var cells = new string[table.Columns.Count];
            for (var i = 0; i < table.Columns.Count; i++)
            {
                cells[i] = row.Cells.TryGetValue(table.Columns[i].Key, out var cell)
                    ? cell.DisplayText
                    : string.Empty;
            }

            output.WriteLine(string.Join("\t", cells));
        }
    }

    private void WriteBranch(RenderTreeBranch branch, int depth)
    {
        var indent = new string(' ', depth * 2);
        var line = string.Join(" ", branch.Row.Cells.Values.Select(c => c.DisplayText));
        output.WriteLine($"{indent}{line}".TrimEnd());

        foreach (var child in branch.Children)
        {
            WriteBranch(child, depth + 1);
        }
    }

    private void WriteSection(RenderNode.Section section, int depth)
    {
        if (!string.IsNullOrEmpty(section.Header))
        {
            output.WriteLine(section.Header);
        }

        foreach (var child in section.Children)
        {
            this.WriteNode(child, depth);
        }
    }

    private void WriteDocument(RenderNode.Document doc, int depth)
    {
        if (!string.IsNullOrEmpty(doc.Kind))
        {
            output.WriteLine($"kind={doc.Kind}");
        }

        foreach (var field in doc.Fields)
        {
            if (field.Audience == RenderAudience.HumanOnly)
            {
                continue;
            }

            // KeyValue inside a Document collapses to "fieldKey=value" so the
            // surrounding field key is the machine name (not the KeyValue's
            // own label).
            if (field.Node is RenderNode.KeyValue kv)
            {
                output.WriteLine($"{field.Key}={kv.Value.DisplayText}");
                continue;
            }

            this.WriteNode(field.Node, depth);
        }
    }
}
