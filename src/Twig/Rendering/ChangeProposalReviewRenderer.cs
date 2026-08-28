using Twig.Domain.Services.ChangeProposals;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>
/// The guaranteed terminal/text fallback presentation for a Change Proposal (Spec #729
/// §Terminal/text fallback, AB#743).
/// <para>
/// This renders the <b>existing</b> canonical semantic review model (design record T2 §4.1,
/// <c>modelVersion</c> 1). It deliberately defines no second model shape: a fallback that
/// projected its own structure would be a second answer to "what is a reviewer shown", which
/// is precisely the ambiguity Spec #729 exists to remove.
/// </para>
/// <para>
/// <b>Adapter rules (T2 §4.3), enforced here:</b>
/// </para>
/// <list type="number">
///   <item>Unknown members within a known <c>modelVersion</c> are ignored — additive evolution
///     stays safe.</item>
///   <item>An unknown <c>modelVersion</c> <b>fails closed</b>: the fallback refuses to render
///     rather than showing a partial proposal. Half a proposal is worse than none, because a
///     reviewer cannot tell which half is missing.</item>
///   <item>Every operation, precondition, consequence and authorization choice is rendered.
///     Eliding a material entry is a compliance failure, not a presentation choice.</item>
///   <item>Enrichment is additive only. Nothing here adds or removes an authorization choice,
///     and the digest is echoed verbatim, never recomputed.</item>
/// </list>
/// </summary>
public static class ChangeProposalReviewRenderer
{
    /// <summary>The only <c>modelVersion</c> this renderer knows how to present in full.</summary>
    public const int SupportedModelVersion = 1;

    /// <summary>
    /// Whether this renderer can present <paramref name="modelVersion"/> in full.
    /// <para>
    /// The in-tree model type pins its version to <see cref="SupportedModelVersion"/>, so today
    /// the negative branch is unreachable from a locally-built model. It is still the load-bearing
    /// rule of T2 §4.3 and is checked — and tested — directly, so that the day a model arrives
    /// from another adapter or a persisted audit row, the fallback refuses instead of quietly
    /// rendering the members it happens to recognise.
    /// </para>
    /// </summary>
    public static bool IsSupported(int modelVersion) => modelVersion == SupportedModelVersion;

    /// <summary>
    /// Renders <paramref name="model"/> as terminal/text lines.
    /// </summary>
    /// <param name="model">The canonical review model to present.</param>
    /// <param name="steering">
    /// The session's steering mode, used only to phrase what happens next. In anything other
    /// than <see cref="SessionSteeringMode.Afk"/> the proposal is held until a human confirms.
    /// </param>
    public static IReadOnlyList<RenderNode> Render(
        ChangeProposalReviewModel model,
        SessionSteeringMode steering)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!IsSupported(model.ModelVersion))
        {
            return
            [
                new RenderNode.Text(
                    $"Cannot review this proposal: review model version {model.ModelVersion} is not supported "
                    + $"(this build understands version {SupportedModelVersion}).",
                    Severity.Error),
                new RenderNode.Text(
                    "Refusing to render a partial proposal — upgrade twig before authorizing this apply.",
                    Severity.Error),
            ];
        }

        var lines = new List<RenderNode>
        {
            new RenderNode.Text("Change Proposal review"),
            // Verbatim from the model. An authorization binds to this exact string.
            new RenderNode.Text($"  digest:    {model.Digest}"),
            new RenderNode.Text($"  workspace: {model.Workspace.Organization}/{model.Workspace.Project}"),
            new RenderNode.Text($"  recipe:    {(model.Recipe is { } r ? $"{r.RecipeId} v{r.Version}" : "(ad hoc)")}"),
            new RenderNode.Text($"  rationale: {model.Rationale ?? "(none)"}"),
        };

        lines.Add(new RenderNode.Text($"affected items ({model.AffectedItems.Count}):"));
        foreach (var item in model.AffectedItems)
        {
            var type = item.Type ?? "(uncached)";
            var title = item.Title ?? "(uncached)";
            var state = item.State ?? "(uncached)";
            lines.Add(new RenderNode.Text($"  #{item.Id} [{item.Role}] {type} — {title} ({state})"));
        }

        lines.Add(new RenderNode.Text($"operations ({model.Operations.Count}):"));
        foreach (var op in model.Operations)
        {
            var target = op.Target.WorkItemId is { } id
                ? $"#{id}"
                : op.Target.StagedIdentity is { } staged ? $"seed {staged}" : "(no target)";
            lines.Add(new RenderNode.Text($"  [{op.Ordinal}] {op.OpId}  {op.Kind}  {target}"));
            lines.Add(new RenderNode.Text($"      {op.Summary}"));

            // Preconditions and consequences are rendered even when empty, so "this operation
            // is bound to nothing" and "we did not show you what it is bound to" cannot be
            // confused for one another.
            lines.Add(new RenderNode.Text($"      preconditions ({op.Preconditions.Count}):"));
            foreach (var pre in op.Preconditions)
                lines.Add(new RenderNode.Text($"        {pre.Kind} = {pre.Value}"));

            lines.Add(new RenderNode.Text($"      consequences ({op.Consequences.Count}):"));
            foreach (var con in op.Consequences)
                lines.Add(new RenderNode.Text($"        {DescribeConsequence(con)}"));
        }

        lines.Add(new RenderNode.Text($"blockers ({model.Blockers.Count}):"));
        foreach (var blocker in model.Blockers)
        {
            var subject = blocker.WorkItemId is { } blockedId ? $"#{blockedId} " : string.Empty;
            lines.Add(new RenderNode.Text($"  {blocker.Kind}: {subject}{blocker.Detail}", Severity.Warning));
        }

        // Rendered exactly as the model supplies them. The fallback never adds a choice the
        // model withheld (a blocked proposal does not offer `apply`) and never removes one.
        lines.Add(new RenderNode.Text(
            $"authorization choices ({model.AuthorizationChoices.Count}): "
            + string.Join(", ", model.AuthorizationChoices)));

        lines.Add(steering == SessionSteeringMode.Afk
            ? new RenderNode.Hint(
                "This session is AFK-steered: apply requires a model authorization record bound to the digest above.")
            : new RenderNode.Hint(
                "Not applied. This session is human-steered: apply requires your sign-off bound to the digest above."));

        return lines;
    }

    private static string DescribeConsequence(ReviewConsequence consequence) => consequence.Kind switch
    {
        "field-set" => $"field-set {consequence.Field} = {consequence.To}",
        "field-clear" => $"field-clear {consequence.Field}",
        "link-add" => $"link-add {consequence.Relation} → #{consequence.OtherId}",
        "link-remove" => $"link-remove {consequence.Relation} → #{consequence.OtherId}",
        "seed-publish" => "seed-publish",
        "work-item-delete" => $"work-item-delete #{consequence.OtherId}",
        // An unrecognised consequence kind is still shown in full rather than dropped: the
        // reviewer must see that something will happen even when this build cannot phrase it.
        _ => $"{consequence.Kind} {consequence.Field} {consequence.To} {consequence.Relation} {consequence.OtherId}".Trim(),
    };
}
