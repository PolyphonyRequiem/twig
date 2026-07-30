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
/// behaviour is inherited. Annotation styles map to <see cref="Severity"/> so the
/// renderer, not this projection, owns colour.
/// </para>
/// </remarks>
internal sealed class WorkingSetTreeProjector(SpectreTheme theme, string iconMode)
{
    internal RenderTree.RenderTree Project(WorkingSetForest forest)
    {
        var structures = forest.Roots
            .Select(root => (RenderNode)new RenderNode.TreeView(BuildBranch(root)))
            .ToList();

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

    private RenderTreeBranch BuildBranch(WorkingSetNode node)
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
        var badge = node.Annotation?.IconId is { } iconId
            ? IconSet.GetIconByIconId(iconMode, iconId) ?? theme.GetTypeBadge(item.Type)
            : theme.GetTypeBadge(item.Type);

        cells["badge"] = RenderCell.DisplayOnly(badge.TrimEnd(), rowSeverity);
        cells["state"] = RenderCell.String(item.State ?? string.Empty, rowSeverity);
        cells["type"] = RenderCell.String(item.Type.Value, rowSeverity);
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

        var children = node.Children.Select(BuildBranch).ToList();
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
}
