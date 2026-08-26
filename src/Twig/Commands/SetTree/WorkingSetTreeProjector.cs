using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands.SetTree;

/// <summary>
/// Projects an annotated working-set forest (twig#277) into a
/// <see cref="RenderTree.RenderTree"/>, so human / json / minimal / ids output all come
/// from the existing rendering pipeline rather than a second hand-rolled formatter.
/// </summary>
/// <remarks>
/// <para>
/// Each structure becomes one <see cref="RenderNode.TreeView"/>; the forest becomes a
/// <see cref="RenderNode.Document"/> with a <c>structures</c> section, so a caller can
/// say "structure 3 of 31" without launching N subprocesses.
/// </para>
/// <para>
/// Glyphs are resolved through <see cref="SpectreTheme.GetTypeBadge"/> and
/// <see cref="IconSet.GetIconByIconId"/> — never hardcoded here — so nerd/unicode/ascii
/// behaviour is inherited. Annotation styles map to <see cref="Severity"/>, and type /
/// state colours travel on <see cref="RenderCell.ThemeColor"/>, so the renderer — not
/// this projection — owns how the two are resolved against each other.
/// </para>
/// <para>
/// This projector also owns the annotation column: it measures each row and pads the
/// title so every note starts in the same column (AB#775). Alignment lives here rather
/// than in the renderer because only the projector knows which cell the note follows.
/// </para>
/// </remarks>
internal sealed class WorkingSetTreeProjector(SpectreTheme theme, string iconMode)
{
    internal RenderTree.RenderTree Project(WorkingSetForest forest)
    {
        var layout = new List<RowLayout>();
        var structures = forest.Roots
            .Select(root => (RenderNode)new RenderNode.TreeView(BuildBranch(root, depth: 0, layout)))
            .ToList();

        AlignAnnotationColumn(layout);

        var fields = new List<DocumentField>
        {
            new("structures", new RenderNode.Section(null, structures)),
            new("structureCount", new RenderNode.KeyValue(
                "structureCount", RenderCell.Integer(forest.Roots.Count)),
                RenderAudience.MachineOnly),
        };

        if (forest.MissingIds.Count > 0)
        {
            // A Table (not a Section of KeyValues) so the JSON projection is a clean
            // array of objects — [{ "id": 101 }, …] — rather than {key,value} pairs.
            fields.Add(new DocumentField(
                "missingIds",
                new RenderNode.Table(
                    null,
                    [new RenderColumn("id", "ID")],
                    forest.MissingIds
                        .Select(id => new RenderRow(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                        {
                            ["id"] = RenderCell.Integer(id),
                        }))
                        .ToList()),
                RenderAudience.MachineOnly));
        }

        return new RenderTree.RenderTree([new RenderNode.Document("workingSetTree", fields)]);
    }

    private RenderTreeBranch BuildBranch(WorkingSetNode node, int depth, List<RowLayout> layout)
    {
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal);
        var severity = SeverityFor(node.Annotation?.Style ?? AnnotationStyle.Default);

        if (node.IsPlaceholder)
        {
            // Unmistakably a placeholder: the reviewer must never mistake a
            // cache miss for a real item they have consented to.
            cells["id"] = RenderCell.Integer(node.Id, $"#{node.Id}");
            cells["title"] = new RenderCell(
                "— not in cache",
                new RenderValue.String("not in cache"),
                Severity.Warning);
            cells["notInCache"] = new RenderCell(string.Empty, new RenderValue.Boolean(true));
            cells["inWorkingSet"] = new RenderCell(string.Empty, new RenderValue.Boolean(node.InWorkingSet));
            AppendAnnotationCells(cells, node.Annotation, severity);
            layout.Add(new RowLayout(cells, depth));
            return new RenderTreeBranch(new RenderRow("workItem", cells), []);
        }

        var item = node.Item!;

        // The ancestor spine is context, not subject (twig#340): it explains where a
        // member lives without being something the reviewer is acting on, so it
        // recedes to dim grey. An explicit caller annotation still wins — if the
        // caller had something to say about a connector, that is not decoration.
        var rowSeverity = node.Annotation is not null
            ? severity
            : node.InWorkingSet ? Severity.None : Severity.Muted;

        // Annotation icon takes the badge slot when supplied, so the caller can flag a
        // node visually; otherwise the item's own type badge is used. Both resolve
        // through IconSet so nerd/unicode/ascii behaviour is inherited.
        //
        // NormalizeBadgeWidth is applied to both arms, not just the type badge:
        // GetIconByIconId deliberately returns the raw glyph so callers can chain a
        // fallback, so the annotation-icon path would otherwise skip normalization and
        // render a nerd badge one cell narrower than a type badge on the row beside it.
        var badge = IconSet.NormalizeBadgeWidth(
            node.Annotation?.IconId is { } iconId
                ? IconSet.GetIconByIconId(iconMode, iconId) ?? theme.GetTypeBadge(item.Type)
                : theme.GetTypeBadge(item.Type));

        // Type and state carry their own colour so an annotated tree reads like
        // `twig show` for the same item. Severity still wins outright for Muted and
        // Error — that precedence is the renderer's call, not this projection's.
        var typeColor = theme.GetTypeMarkupColor(item.Type);
        var state = item.State ?? string.Empty;

        cells["badge"] = RenderCell.DisplayOnly(badge, rowSeverity) with { ThemeColor = typeColor };
        cells["state"] = RenderCell.String(state, rowSeverity) with
        {
            ThemeColor = theme.GetStateCategoryMarkupColor(state),
        };
        cells["type"] = RenderCell.String(item.Type.Value, rowSeverity) with { ThemeColor = typeColor };
        cells["id"] = RenderCell.Integer(item.Id, $"#{item.Id}", rowSeverity);
        cells["title"] = RenderCell.String(item.Title ?? string.Empty, rowSeverity);
        cells["parentId"] = item.ParentId.HasValue
            ? new RenderCell(string.Empty, new RenderValue.Integer(item.ParentId.Value))
            : new RenderCell(string.Empty, new RenderValue.Null());

        // A machine consumer must be able to tell a node the caller asked about from a
        // connecting ancestor twig pulled in — the review decision only covers the
        // former. Machine-only: empty DisplayText keeps it out of the human row.
        cells["inWorkingSet"] = new RenderCell(string.Empty, new RenderValue.Boolean(node.InWorkingSet));

        AppendAnnotationCells(cells, node.Annotation, severity);
        layout.Add(new RowLayout(cells, depth));

        var children = node.Children.Select(child => BuildBranch(child, depth + 1, layout)).ToList();
        return new RenderTreeBranch(new RenderRow("workItem", cells), children);
    }

    private static void AppendAnnotationCells(
        Dictionary<string, RenderCell> cells,
        TreeAnnotation? annotation,
        Severity severity)
    {
        if (annotation is null)
            return;

        if (!string.IsNullOrEmpty(annotation.Note))
        {
            cells["note"] = new RenderCell(
                $"└ {annotation.Note}",
                new RenderValue.String(annotation.Note),
                severity);
        }

        cells["style"] = RenderCell.DisplayOnly(string.Empty) with
        {
            Value = new RenderValue.String(AnnotationStyleParser.ToWireName(annotation.Style)),
        };

        if (annotation.IconId is not null)
        {
            cells["icon"] = RenderCell.DisplayOnly(string.Empty) with
            {
                Value = new RenderValue.String(annotation.IconId),
            };
        }
    }

    /// <summary>
    /// Maps the annotation style vocabulary onto the render tree's renderer-agnostic
    /// <see cref="Severity"/>. <see cref="AnnotationStyle.Muted"/> maps to
    /// <see cref="Severity.Muted"/>, added in twig#340 — before that the severity
    /// vocabulary had no "dim" member and muted rendered uncoloured.
    /// </summary>
    private static Severity SeverityFor(AnnotationStyle style) => style switch
    {
        AnnotationStyle.Muted => Severity.Muted,
        AnnotationStyle.Proposed => Severity.Info,
        AnnotationStyle.Warn => Severity.Warning,
        AnnotationStyle.Error => Severity.Error,
        _ => Severity.None,
    };

    /// <summary>
    /// A built row plus the tree depth it will be drawn at, retained so the annotation
    /// column can be measured once every row exists.
    /// </summary>
    /// <remarks>
    /// Holds the live cell dictionary rather than the <see cref="RenderRow"/> wrapping
    /// it, so alignment can rewrite a cell in place without rebuilding the branch tree.
    /// </remarks>
    private sealed record RowLayout(Dictionary<string, RenderCell> Cells, int Depth);

    /// <summary>
    /// Columns Spectre's tree guide spends per level of depth (<c>"├── "</c>,
    /// <c>"│   "</c>). Indentation shifts every following column — that is the tree's
    /// core affordance and AB#775 keeps it — so depth has to be folded into the
    /// measurement rather than aligned away.
    /// </summary>
    private const int TreeIndentWidth = 4;

    /// <summary>
    /// Pads each annotated row's title so every note begins in the same column
    /// (AB#775). Only the annotation column is aligned; badge, state, type and id keep
    /// shifting with depth, because a fixed gutter would spend columns at depth zero to
    /// buy alignment nobody reads across, and would need a max-depth policy that
    /// silently stops encoding depth once clamped.
    /// </summary>
    private static void AlignAnnotationColumn(List<RowLayout> layout)
    {
        var target = 0;
        foreach (var row in layout)
        {
            if (row.Cells.ContainsKey("note"))
            {
                target = Math.Max(target, NoteColumn(row));
            }
        }

        if (target == 0)
        {
            return;
        }

        foreach (var row in layout)
        {
            if (!row.Cells.ContainsKey("note"))
            {
                continue;
            }

            var pad = target - NoteColumn(row);
            if (pad <= 0)
            {
                continue;
            }

            // Pad the last cell the human actually sees before the note. The machine
            // Value is left untouched: padding is presentation, and a JSON consumer
            // must not have to trim it back off.
            var key = LastVisibleKeyBeforeNote(row.Cells);
            if (key is not null)
            {
                var cell = row.Cells[key];
                row.Cells[key] = cell with { DisplayText = cell.DisplayText + new string(' ', pad) };
            }
        }
    }

    /// <summary>
    /// The column the note currently starts in: the tree indent for this row's depth,
    /// plus every visible cell before the note, plus the renderer's two-space joins.
    /// </summary>
    private static int NoteColumn(RowLayout row)
    {
        var width = row.Depth * TreeIndentWidth;
        var visible = 0;
        foreach (var (key, cell) in row.Cells)
        {
            if (string.Equals(key, "note", StringComparison.Ordinal))
            {
                break;
            }

            // Machine-only cells carry empty DisplayText and the renderer skips them,
            // so they contribute neither width nor a separator.
            if (cell.DisplayText.Length == 0)
            {
                continue;
            }

            if (visible > 0)
            {
                width += 2;
            }

            width += DisplayWidth.Measure(cell.DisplayText);
            visible++;
        }

        return width;
    }

    private static string? LastVisibleKeyBeforeNote(Dictionary<string, RenderCell> cells)
    {
        string? last = null;
        foreach (var (key, cell) in cells)
        {
            if (string.Equals(key, "note", StringComparison.Ordinal))
            {
                break;
            }

            if (cell.DisplayText.Length > 0)
            {
                last = key;
            }
        }

        return last;
    }
}
