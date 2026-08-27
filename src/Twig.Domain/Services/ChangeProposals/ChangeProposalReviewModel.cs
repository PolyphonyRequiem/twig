using Twig.Domain.Services.Plan;

namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// The canonical semantic review model — the sole source of truth for what a reviewer must
/// be shown before authorizing a Change Proposal. Shape fixed by design record T2 (AB#741)
/// as <c>modelVersion</c> 1.
/// <para>
/// <b>Derived, never hashed.</b> The model embeds <see cref="Digest"/>; it never contributes
/// to it. <see cref="AffectedItems"/> carries live titles and states, which change over
/// time — if the model were hashed into the proposal, an unrelated title edit would change
/// the digest and invalidate an authorization that was still perfectly valid. So the model
/// is reproducible but not immutable: the same proposal yields the same operations,
/// preconditions and consequences on any adapter, while affected-item context reflects the
/// board at render time.
/// </para>
/// <para>
/// <b>Adapter rules.</b> A renderer MUST ignore unknown members within a known
/// <see cref="ModelVersion"/>, MUST fail closed on an unknown version rather than partially
/// render, and MUST render every entry of <see cref="Operations"/>,
/// <see cref="ReviewOperation.Preconditions"/>, <see cref="ReviewOperation.Consequences"/>
/// and <see cref="AuthorizationChoices"/>. Eliding a material entry is a compliance failure,
/// not a presentation choice. Enrichment is additive only: an adapter may never add or
/// remove an authorization choice, and never alter the digest.
/// </para>
/// </summary>
public sealed record ChangeProposalReviewModel
{
    /// <summary>Constant discriminator identifying this model.</summary>
    public string Model => "twig.change-proposal.review";

    /// <summary>
    /// Model schema version. Incremented ONLY on a breaking change; additive optional
    /// members do not increment it.
    /// </summary>
    public int ModelVersion => 1;

    /// <summary>
    /// The proposal's digest, verbatim. A renderer never recomputes this — it is the value
    /// an authorization is bound to.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>The workspace the proposal targets, from the document.</summary>
    public required PlanWorkspace Workspace { get; init; }

    /// <summary>Why the proposal exists; <c>null</c> when the author supplied nothing.</summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// The recipe this proposal was rendered from, or <c>null</c> when it is ad hoc.
    /// <para>
    /// Additive optional member within <c>modelVersion</c> 1 — permitted by T2 §4.2, which
    /// bumps the version only for breaking changes. It is present because Spec #729 requires
    /// a reviewer to be able to navigate from a proposal back to its template, and to tell an
    /// ad hoc proposal from a rendered one.
    /// </para>
    /// </summary>
    public ChangeRecipeReference? Recipe { get; init; }

    /// <summary>
    /// Every work item the proposal touches: operation targets plus link peers, enriched
    /// with the type/title/state known locally.
    /// </summary>
    public required IReadOnlyList<ReviewAffectedItem> AffectedItems { get; init; }

    /// <summary>One entry per proposal operation, in declared order.</summary>
    public required IReadOnlyList<ReviewOperation> Operations { get; init; }

    /// <summary>
    /// The authorization choices actually available for this proposal. A proposal that
    /// cannot currently apply does not offer <c>apply</c> — offering a control that is
    /// guaranteed to refuse misrepresents the decision the reviewer is making.
    /// </summary>
    public required IReadOnlyList<string> AuthorizationChoices { get; init; }

    /// <summary>Everything standing between this proposal and applying. May be empty.</summary>
    public required IReadOnlyList<ReviewBlocker> Blockers { get; init; }
}

/// <summary>A work item the proposal affects, with the context a reviewer needs to recognise it.</summary>
public sealed record ReviewAffectedItem
{
    /// <summary>The work item id.</summary>
    public required int Id { get; init; }

    /// <summary>Work item type name, or <c>null</c> when the item is not in the local cache.</summary>
    public string? Type { get; init; }

    /// <summary>Title, or <c>null</c> when the item is not in the local cache.</summary>
    public string? Title { get; init; }

    /// <summary>Current state, or <c>null</c> when the item is not in the local cache.</summary>
    public string? State { get; init; }

    /// <summary>
    /// <c>target</c> when an operation acts directly on this item; <c>peer</c> when it is
    /// only the far end of a link being added or removed.
    /// </summary>
    public required string Role { get; init; }
}

/// <summary>One operation, described semantically rather than as raw payload.</summary>
public sealed record ReviewOperation
{
    /// <summary>Zero-based position in the proposal's declared execution order.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The document-unique operation id.</summary>
    public required string OpId { get; init; }

    /// <summary>Wire kind: <c>batch|add-link|remove-link|publish-seed|delete</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>What this operation acts on.</summary>
    public required ReviewTarget Target { get; init; }

    /// <summary>Short semantic phrase describing the operation in one line.</summary>
    public required string Summary { get; init; }

    /// <summary>What must still hold at apply time for this operation to proceed. May be empty.</summary>
    public required IReadOnlyList<ReviewPrecondition> Preconditions { get; init; }

    /// <summary>What this operation will change if it proceeds. May be empty.</summary>
    public required IReadOnlyList<ReviewConsequence> Consequences { get; init; }
}

/// <summary>
/// What an operation acts on. Carries <see cref="WorkItemId"/> for every kind except
/// <c>publish-seed</c>, which carries <see cref="StagedIdentity"/> instead — a seed has no
/// work item id until it is published, and the model must not pretend otherwise.
/// </summary>
public sealed record ReviewTarget
{
    /// <summary>The target work item id; <c>null</c> for a <c>publish-seed</c> operation.</summary>
    public int? WorkItemId { get; init; }

    /// <summary>The staged seed identity; <c>null</c> for every kind except <c>publish-seed</c>.</summary>
    public string? StagedIdentity { get; init; }
}

/// <summary>A condition checked at apply time; a mismatch refuses the operation.</summary>
public sealed record ReviewPrecondition
{
    /// <summary><c>expectedRevision</c> or <c>expectedFingerprint</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The exact value the operation is bound to.</summary>
    public required string Value { get; init; }
}

/// <summary>One semantic effect the operation will have.</summary>
public sealed record ReviewConsequence
{
    /// <summary>
    /// <c>field-set</c>, <c>field-clear</c>, <c>link-add</c>, <c>link-remove</c>,
    /// <c>seed-publish</c>, or <c>work-item-delete</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Field reference name, for field consequences.</summary>
    public string? Field { get; init; }

    /// <summary>The value the field is being set to; <c>null</c> when the field is being cleared.</summary>
    public string? To { get; init; }

    /// <summary>Relation name, for link consequences.</summary>
    public string? Relation { get; init; }

    /// <summary>The far end of a link, or the item being deleted.</summary>
    public int? OtherId { get; init; }
}

/// <summary>Something preventing this proposal from applying right now.</summary>
public sealed record ReviewBlocker
{
    /// <summary><c>pending</c> for a staged local edit, <c>issue</c> for a validation issue.</summary>
    public required string Kind { get; init; }

    /// <summary>The work item involved, when the blocker names one.</summary>
    public int? WorkItemId { get; init; }

    /// <summary>Human-readable explanation.</summary>
    public required string Detail { get; init; }
}
