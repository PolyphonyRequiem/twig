using Twig.Domain.ValueObjects;

namespace Twig.Domain.Projections;

/// <summary>
/// One proposed edit to a single field, carrying both the value the caller started from
/// and the value it wants to write.
/// </summary>
/// <param name="FieldRef">
/// The field's reference name (for example <c>System.Title</c>). This is the join key
/// between a proposal, a <see cref="DetailControl.Id"/>, and an
/// <see cref="EditCapability"/>.
/// </param>
/// <param name="PriorValue">
/// The value the caller started from. <c>null</c> is a legitimate prior value meaning the
/// field was empty, and is NOT a "no prior value known" sentinel.
/// </param>
/// <param name="ProposedValue">The value the caller wants written. <c>null</c> clears the field.</param>
/// <remarks>
/// 🔴 <b><see cref="PriorValue"/> is NOT the concurrency check.</b> Concurrency is
/// revision-based — see <see cref="EditConflict.RemoteRevision"/>. The prior value exists so
/// a host can render <i>what changed</i> rather than what the field now is, confirm before
/// saving, and undo without a re-fetch. An implementer who compares prior values to detect
/// collisions has built last-write-wins, which is a strictly weaker guarantee than the
/// revision check this contract actually relies on.
/// </remarks>
public sealed record FieldEdit(
    string FieldRef,
    string? PriorValue,
    string? ProposedValue);

/// <summary>
/// A proposed move from one workflow state to another, optionally carrying field edits that
/// accompany the move.
/// </summary>
/// <param name="FromState">The state the caller believes the item is in.</param>
/// <param name="ToState">The state the caller wants to move to.</param>
/// <param name="Accompanying">
/// Field edits applied as part of the same unit of work — for example a resolution reason
/// set while closing. Empty when the move stands alone.
/// </param>
/// <remarks>
/// <para>
/// <b>Why this is its own change kind and not a <see cref="FieldEdit"/> named
/// <c>System.State</c>.</b> 🔴 The wire does NOT justify it: Azure DevOps takes one JSON
/// patch of field values and <c>System.State</c> sits in it like any other field. That is
/// recorded explicitly so nobody later "simplifies" this contract back to a uniform field
/// list on the grounds that the server does not distinguish them.
/// </para>
/// <para>
/// The <i>behaviour</i> justifies it. A rejected direct transition is retried by walking
/// intermediate states, one PATCH per hop, so a single user-visible state move can become
/// several writes with a traversed path to report. No ordinary field change ever does this.
/// Twig's existing vocabulary already splits them the same way — the pending-change store
/// distinguishes a <c>state</c> row from a <c>field</c> row, and the mutation provider has
/// separate entry points.
/// </para>
/// </remarks>
public sealed record StateMove(
    string FromState,
    string ToState,
    IReadOnlyList<FieldEdit> Accompanying);

/// <summary>
/// A change a host proposes to a work item: either a field edit or a state move.
/// </summary>
/// <remarks>
/// Pattern-match the case (<c>proposal is StateMove move</c>). Note that
/// <c>ShouldBeOfType&lt;StateMove&gt;()</c> fails against the union wrapper — use
/// <c>Twig.TestKit.UnionAssertions.ShouldBeUnionCase</c> in tests.
/// </remarks>
public union ChangeProposal(FieldEdit, StateMove);

/// <summary>
/// One field whose remote value moved out from under a proposed change.
/// </summary>
/// <param name="FieldRef">The field's reference name.</param>
/// <param name="PriorValue">The value the caller started from.</param>
/// <param name="ProposedValue">The value the caller tried to write.</param>
/// <param name="RemoteValue">The value on the server now — the fact Twig did not previously carry.</param>
public sealed record ConflictedField(
    string FieldRef,
    string? PriorValue,
    string? ProposedValue,
    string? RemoteValue);

/// <summary>
/// Reports that a proposed change collided with a newer remote revision, carrying enough
/// detail for a host to resolve the collision itself.
/// </summary>
/// <param name="RemoteRevision">
/// 🔴 <b>The concurrency check.</b> This — not any value comparison — is what determines
/// whether a write is safe. A resolver that diffs <see cref="ConflictedField.PriorValue"/>
/// against <see cref="ConflictedField.RemoteValue"/> to decide whether to overwrite has
/// implemented last-write-wins and lost the guarantee this type exists to preserve.
/// </param>
/// <param name="Fields">
/// Only the fields actually in collision, not the whole form.
/// </param>
/// <remarks>
/// <para>
/// <b>Why this is not a <see cref="WorkItemDetailDocument"/>,</b> despite also carrying
/// remote values. Three reasons, in order of weight:
/// </para>
/// <list type="number">
/// <item>A document cannot be built without a <see cref="FormLayout"/>. A collision is
/// detected at save time, in a sink, which has no layout and no reason to acquire one —
/// reusing the document would make the <i>error</i> path require a round trip the
/// <i>success</i> path does not.</item>
/// <item>A form has hundreds of controls; a collision has one to three. Shipping the whole
/// form to report three fields is the wrong ratio, and the host would then have to diff two
/// documents to find what actually collided.</item>
/// <item>It stays honest about what a sink knows: the remote <i>values</i>, not the remote
/// <i>arrangement</i>.</item>
/// </list>
/// <para>
/// A host that wants this document-shaped can project it itself — it holds the layout and
/// the sink does not.
/// </para>
/// </remarks>
public sealed record EditConflict(
    int RemoteRevision,
    IReadOnlyList<ConflictedField> Fields);

/// <summary>
/// The change was written; <see cref="Revision"/> is the server revision it was BASED ON.
/// </summary>
/// <remarks>
/// Not a new revision the sink minted. A sink cannot know what revision the server will assign,
/// and a staging or queueing sink has not written to the server at all — so this reports where
/// the item still is. See <see cref="IChangeSink.SubmitAsync"/>.
/// </remarks>
public sealed record Saved(int Revision);

/// <summary>The change collided with a newer remote revision after the sink's retry.</summary>
public sealed record Conflicted(EditConflict Conflict);

/// <summary>
/// The change was refused — by the sink's own validation, or by the server.
/// </summary>
/// <remarks>
/// A refused state transition lands here. 🔴 Twig's offered transitions are advisory
/// (see <see cref="EditCapability.OfferedStates"/>), so a refusal here is a legitimate
/// server answer and NOT evidence of a Twig defect.
/// </remarks>
public sealed record Refused(string Reason);

/// <summary>Outcome of submitting a <see cref="ChangeProposal"/> to an <see cref="IChangeSink"/>.</summary>
public union SubmitOutcome(Saved, Conflicted, Refused);
