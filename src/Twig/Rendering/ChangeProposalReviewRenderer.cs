using System.Globalization;
using System.Text;
using Twig.Domain.Services.ChangeProposals;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>
/// Projects the canonical review model into a human-focused grouped terminal layout. Material
/// consequences, warnings, blockers and choices survive both densities; machine bookkeeping stays
/// in the canonical model and JSON. Only description bodies may be elided in brief.
/// </summary>
public static class ChangeProposalReviewRenderer
{
    /// <summary>The only supported semantic model version.</summary>
    public const int SupportedModelVersion = 1;
    /// <summary>Unknown versions must not be partially presented.</summary>
    public static bool IsSupported(int modelVersion) => modelVersion == SupportedModelVersion;
    /// <summary>Builds a brief review without changing authorization policy.</summary>
    public static IReadOnlyList<RenderNode> Render(ChangeProposalReviewModel model, SessionSteeringMode steering)
        => Render(model, steering, false);

    /// <summary>Builds brief or full output from the same captured review object.</summary>
    public static IReadOnlyList<RenderNode> Render(ChangeProposalReviewModel model, SessionSteeringMode steering, bool full)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!IsSupported(model.ModelVersion))
            return [new RenderNode.Text($"Cannot review model version {model.ModelVersion}. Refusing partial review; upgrade twig.", Severity.Error)];

        var lines = new List<RenderNode>();
        if (model.Recipe is { } recipe)
            lines.Add(Text($"recipe: {recipe.RecipeId} v{recipe.Version}"));
        if (!string.IsNullOrWhiteSpace(model.Rationale))
            lines.Add(Text($"rationale: {model.Rationale}"));

        var items = model.ContextItems.Concat(model.AffectedItems).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last());
        // One derived width across all siblings. The render provider additionally bounds it by terminal width.
        var labelWidth = model.Operations.SelectMany(o => o.Consequences)
            .Where(c => c.Field is not null).Select(c => Spectre.Console.Rendering.Segment.CellCount([new Spectre.Console.Rendering.Segment(Safe(c.FieldLabel ?? c.Field!))])).DefaultIfEmpty(1).Max();
        var groups = new List<(ReviewTarget Target, List<RenderNode> Body)>();
        foreach (var op in model.Operations)
        {
            // Only adjacent same-target operations coalesce. A,B,A stays A,B,A, never A,A,B.
            if (groups.Count == 0 || !SameTarget(groups[^1].Target, op.Target)) groups.Add((op.Target, []));
            var body = groups[^1].Body;
            foreach (var con in op.Consequences)
            {
                if (con.Field is not null && con.Kind is "field-set" or "field-clear")
                    body.Add(new RenderNode.FieldBlock([new RenderNode.KeyValue(Safe(con.FieldLabel ?? con.Field),
                        DescribeField(con, full))], labelWidth));
                else body.Add(Text(DescribeConsequence(con)));
            }
            foreach (var warning in ObservationWarnings(op))
                body.Add(new RenderNode.Text(warning, Severity.Warning));
        }
        // Only real item relationships produce tree edges. The bounded context can repeat
        // when needed to preserve declared execution order; no graph traversal can lose an op.
        var roots = new List<RenderTreeBranch>();
        foreach (var group in groups)
        {
            var item = group.Target.WorkItemId is { } id ? items.GetValueOrDefault(id) : null;
            var parentId = item?.ParentId ?? group.Target.Seed?.ParentId;
            var branch = new RenderTreeBranch(Identity(group.Target, item), []) { Body = group.Body };
            if (parentId is { } parent && parent != group.Target.WorkItemId && items.TryGetValue(parent, out var context))
            {
                if (roots.Count > 0 && AttachToTail(roots[^1], parent, branch) is { } attached)
                    roots[^1] = attached;
                else
                    roots.Add(new RenderTreeBranch(Identity(new ReviewTarget { WorkItemId = parent }, context with { Role = "context" }), [branch]));
            }
            else roots.Add(branch);
        }
        var represented = groups.Where(g => g.Target.WorkItemId is not null).Select(g => g.Target.WorkItemId!.Value).ToHashSet();
        foreach (var peer in model.AffectedItems.Where(i => !represented.Contains(i.Id)))
            roots.Add(new RenderTreeBranch(Identity(new ReviewTarget { WorkItemId = peer.Id }, peer), []));
        var shown = new HashSet<int>();
        void Remember(RenderTreeBranch branch)
        {
            if (RowId(branch) is { } id) shown.Add(id);
            foreach (var child in branch.Children) Remember(child);
        }
        foreach (var root in roots) Remember(root);
        foreach (var context in model.ContextItems.Where(c => !shown.Contains(c.Id)))
            roots.Add(new RenderTreeBranch(Identity(new ReviewTarget { WorkItemId = context.Id }, context), []));
        foreach (var root in roots) lines.Add(new RenderNode.TreeView(root));

        if (model.Blockers.Count > 0)
        {
            lines.Add(Text($"blockers ({model.Blockers.Count}):"));
            foreach (var blocker in model.Blockers)
                lines.Add(new RenderNode.Text(DescribeBlocker(blocker), Severity.Warning));
        }

        lines.Add(Text($"authorization choices ({model.AuthorizationChoices.Count}): {string.Join(", ", model.AuthorizationChoices)}"));
        lines.Add(Text("Legend: → = field change; (clear) = remove field; absent = known null; unknown = unavailable baseline; \"\" = empty string. Context and peers are not mutation targets."));
        if (!full && model.Operations.SelectMany(o => o.Consequences)
            .Any(c => string.Equals(c.Field, "System.Description", StringComparison.OrdinalIgnoreCase)))
            lines.Add(Text("Description summaries count removed and inserted Unicode characters. Use --full or Details to show complete bodies."));
        lines.Add(steering == SessionSteeringMode.Afk
            ? new RenderNode.Hint("This session is AFK-steered: apply requires a model authorization record bound to this proposal's exact digest.")
            : new RenderNode.Hint("Not applied. This session is human-steered: apply requires your sign-off bound to this proposal's exact digest."));
        return lines;
    }

    private static IEnumerable<string> ObservationWarnings(ReviewOperation operation)
    {
        foreach (var reason in operation.Consequences
                     .Select(c => c.Before)
                     .Where(before => before?.State == "unknown")
                     .Select(before => before!.Reason)
                     .Distinct(StringComparer.Ordinal))
        {
            yield return reason switch
            {
                "item-not-cached" => "warning: previous value unavailable because the item has not been loaded",
                "revision-mismatch" => "warning: previous value unavailable because the item changed since this proposal was prepared",
                "local-edits" => "warning: previous value unavailable because local changes are staged for this item",
                "field-not-cached" => "warning: previous value unavailable because the field has not been loaded",
                _ => "warning: previous value unavailable",
            };
        }
    }

    private static string DescribeBlocker(ReviewBlocker blocker)
    {
        var target = blocker.WorkItemId is { } id ? $" for #{id}" : string.Empty;
        var prefix = blocker.Kind switch
        {
            "pending" => "Pending local change",
            "issue" => "Review issue",
            _ => "Review blocker",
        };
        return string.IsNullOrWhiteSpace(blocker.Detail)
            ? $"{prefix}{target}"
            : $"{prefix}{target}: {Safe(blocker.Detail)}";
    }

    // Only the last-descendant chain can accept another child without moving an earlier op.
    private static RenderTreeBranch? AttachToTail(RenderTreeBranch root, int parent, RenderTreeBranch child)
    {
        if (root.Children.Count > 0 && AttachToTail(root.Children[^1], parent, child) is { } nested)
            return root with { Children = [.. root.Children.Take(root.Children.Count - 1), nested] };
        return RowId(root) == parent ? root with { Children = [.. root.Children, child] } : null;
    }

    private static int? RowId(RenderTreeBranch branch) => branch.Row.Cells.TryGetValue("id", out var c) && c.Value is RenderValue.Integer n ? (int)n.Value : null;
    private static bool SameTarget(ReviewTarget a, ReviewTarget b) => a.WorkItemId == b.WorkItemId && a.StagedIdentity == b.StagedIdentity;
    private static string TargetName(ReviewTarget target) => target.WorkItemId is { } id ? $"#{id}" : $"seed {target.StagedIdentity ?? "(unknown)"}";
    private static RenderRow Identity(ReviewTarget target, ReviewAffectedItem? item)
    {
        var seed = target.Seed;
        var cells = new Dictionary<string, RenderCell>
        {
            ["identity"] = RenderCell.String(Safe(TargetName(target))),
            ["type"] = RenderCell.String(Safe(item?.Type ?? seed?.Type ?? "(uncached)")),
            ["title"] = RenderCell.String(Safe(item?.Title ?? seed?.Title ?? "(uncached)")),
            ["state"] = RenderCell.String(Safe(item?.State ?? seed?.State ?? "(uncached)")),
            ["role"] = RenderCell.String(item?.Role ?? "staged draft"),
        };
        if (target.WorkItemId is { } id) cells["id"] = new RenderCell("", new RenderValue.Integer(id));
        if (item?.Url is { } url) cells["url"] = RenderCell.String(Safe(url));
        if (seed is not null) cells["alias"] = RenderCell.String($"local alias {seed.DisplayAlias}");
        if ((item?.ParentId ?? seed?.ParentId) is { } parent) cells["parent"] = RenderCell.String($"cached parent #{parent}");
        return new RenderRow("review-item", cells);
    }

    private static RenderCell DescribeField(ReviewConsequence con, bool full)
    {
        var spans = new List<RenderTextSpan>();
        if (!full && string.Equals(con.Field, "System.Description", StringComparison.OrdinalIgnoreCase))
        {
            spans.Add(new(con.Kind == "field-clear" ? "(clear) " : con.To == "" ? "→ \"\" " : "→ replace body "));
            if (con.TextChange is { } metric)
            {
                spans.Add(new($"−{metric.Removed}", RenderTextRole.Before));
                spans.Add(new(" / "));
                spans.Add(new($"+{metric.Inserted}", RenderTextRole.After));
                spans.Add(new(" scalars"));
            }
            else spans.Add(new("(change size unknown)"));
        }
        else
        {
            var before = con.Before?.State switch { "value" => Quote(con.Before.Value ?? ""), "absent" => "(absent)", _ => "(unknown)" };
            spans.Add(new(Safe(before), RenderTextRole.Before));
            spans.Add(new(" → "));
            spans.Add(new(Safe(con.Kind == "field-clear" ? "(clear)" : Quote(con.To ?? "")), RenderTextRole.After));
        }
        var text = string.Concat(spans.Select(s => s.Text));
        return RenderCell.String(text) with { Spans = spans };
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    private static string DescribeConsequence(ReviewConsequence c) => c.Kind switch
    {
        "link-add" => $"add {c.Relation} link to #{c.OtherId}",
        "link-remove" => $"remove {c.Relation} link to #{c.OtherId}",
        "seed-publish" => "publish staged draft (new published identity assigned only when applied)",
        "work-item-delete" => $"delete work item #{c.OtherId}",
        _ => $"change {c.Field ?? "item"} {c.To ?? string.Empty} {c.Relation} {c.OtherId}".Trim(),
    };
    private static RenderNode.Text Text(string text) => new(Safe(text));
    // Preserve printable exact values and line breaks; neutralize terminal controls and bidi spoofing.
    internal static string Safe(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
            if ((char.IsControl(ch) && ch != '\n') || char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                result.Append($"\\u{(int)ch:x4}");
            else result.Append(ch);
        return result.ToString();
    }
}
