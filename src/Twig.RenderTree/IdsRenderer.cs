using System.Globalization;
using System.IO;

namespace Twig.RenderTree;

/// <summary>
/// Renders a <see cref="RenderTree"/> as bare numeric IDs, one per line —
/// suitable for shell piping (<c>twig workspace -o ids | xargs ...</c>).
/// </summary>
/// <remarks>
/// <para>
/// Extraction rule: walk every node depth-first, and for every cell whose
/// dictionary key is <c>"id"</c> and whose <see cref="RenderCell.Value"/>
/// is <see cref="RenderValue.Integer"/>, emit the integer on its own line.
/// </para>
/// <para>
/// The convention "cells keyed <c>id</c> are IDs" is deliberately strict —
/// it lets commands stay generic by naming the field appropriately rather
/// than tagging cells with a separate role. Cells keyed <c>parentId</c>,
/// <c>sourceId</c>, etc. are NOT emitted.
/// </para>
/// </remarks>
public sealed class IdsRenderer(TextWriter output) : IRenderer
{
    /// <summary>The cell-key convention this renderer treats as a primary ID.</summary>
    public const string IdKey = "id";

    public void Render(RenderTree tree)
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
            case RenderNode.Text:
            case RenderNode.Markup:
            case RenderNode.Hint:
            case RenderNode.KeyValue:
                // Free-floating text / markup / hints / labelled values carry no
                // row context, so they're not eligible for ID extraction.
                break;
            case RenderNode.Record rec:
                this.TryWriteId(rec.Fields);
                break;
            case RenderNode.Table table:
                foreach (var row in table.Rows)
                {
                    this.TryWriteId(row.Cells);
                }
                break;
            case RenderNode.TreeView treeView:
                this.WriteBranch(treeView.Root);
                break;
            case RenderNode.Section section:
                foreach (var child in section.Children)
                {
                    this.WriteNode(child);
                }
                break;
            case RenderNode.Document doc:
                foreach (var field in doc.Fields)
                {
                    if (field.Audience == RenderAudience.HumanOnly)
                    {
                        continue;
                    }

                    this.WriteNode(field.Node);
                }
                break;
        }
    }

    private void WriteBranch(RenderTreeBranch branch)
    {
        this.TryWriteId(branch.Row.Cells);
        foreach (var child in branch.Children)
        {
            this.WriteBranch(child);
        }
    }

    private void TryWriteId(IReadOnlyDictionary<string, RenderCell> cells)
    {
        if (cells.TryGetValue(IdKey, out var cell) && cell.Value is RenderValue.Integer integer)
        {
            output.WriteLine(integer.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
