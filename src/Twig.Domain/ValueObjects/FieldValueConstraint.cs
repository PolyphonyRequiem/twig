namespace Twig.Domain.ValueObjects;

/// <summary>
/// What the description says about whether a field's value is restricted to a list — in a
/// form that can carry "we could not find out" rather than collapsing it into "unrestricted".
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>A bare boolean would lie in the OVERSTATING direction, and a two-way answer would
/// lie in the understating one.</b> This is the mirror of
/// <see cref="FieldRequiredness"/> (AB#236): where requiredness could UNDERSTATE what a
/// process demands, a value constraint can OVERSTATE it — telling a caller its value must
/// come from a list when the server accepts anything.
/// </para>
/// <para>
/// So the answer is three-way, not two-way:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="Unconstrained"/> — the server states, explicitly, that the field is not
/// list-backed (<c>isPicklist: false</c>). 🔴 This is a FACT read off the API, never a guess
/// from the field's name or type.
/// </description></item>
/// <item><description>
/// <see cref="ListConstrained"/> — the field is backed by a picklist the server ENFORCES, and
/// <see cref="Values"/> carries that list's resolved contents.
/// </description></item>
/// <item><description>
/// <see cref="ListSuggested"/> — a picklist offers values in the editor but the server accepts
/// anything. 🔴 Distinct from <see cref="ListConstrained"/> because reporting it as enforced
/// is the OVERSTATEMENT this type exists to prevent: it would tell a caller its value must
/// come from the list while a write of anything else succeeds. The values are still carried —
/// they are true and useful — but the claim attached to them is weaker.
/// </description></item>
/// <item><description>
/// <see cref="Unknown"/> — the picklist source could not be read. 🔴 Distinct from
/// <see cref="Unconstrained"/> on purpose: collapsing a failed fetch into "unconstrained"
/// would be exactly the lie this type exists to prevent, and it would carry no notice. A
/// field in this state also puts <c>picklists</c> in its type's unfetched list.
/// </description></item>
/// </list>
/// <para>
/// 🔴 <b>NO NAME-MATCHING HEURISTIC, anywhere.</b> It is not merely undesirable, it is
/// unnecessary: <c>_apis/wit/fields</c> returns <c>isPicklist</c> on every field, so the
/// document states "not list-constrained" as a server fact rather than as an inference from
/// a field being called <c>Status</c> or typed <c>string</c>. An implementation that guesses
/// from a name would be wrong on this org's own data in both directions.
/// </para>
/// <para>
/// 🔴 <b><see cref="Values"/> is an ordered list sorted by the assembler on an ORDINAL key,
/// and that is load-bearing.</b> Picklist items arrive from the server in the order whoever
/// authored the list happened to type them, and that order carries no meaning Twig can
/// defend. Carrying it into the document unsorted would break byte-stability — the single
/// most important property of this feature — in a way no single-run unit test would catch.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Implementation Decision 5(b), Solution S2. Evidence:
/// <c>wayfinder-process-descriptor/assets/0005-picklist-association-findings.md</c>.
/// </para>
/// </remarks>
/// <param name="Kind">Which of the three cases this is.</param>
/// <param name="ListName">
/// The picklist's name where one backs the field, else <c>null</c>. Carried for the reader's
/// orientation only — the association is keyed by id, never by name, because two lists may
/// share a name and a name is not identity.
/// </param>
/// <param name="Values">
/// The list's resolved contents, sorted ordinal. Empty unless <see cref="Kind"/> is
/// <see cref="FieldValueConstraintKind.ListConstrained"/>.
/// <para>
/// 🔴 Empty while <see cref="Kind"/> is <see cref="FieldValueConstraintKind.ListConstrained"/>
/// is a real and different state: a picklist that exists and holds nothing constrains the
/// field to nothing. It is not the same as an unresolved list, which is
/// <see cref="FieldValueConstraintKind.Unknown"/>.
/// </para>
/// </param>
internal sealed record FieldValueConstraint(
    FieldValueConstraintKind Kind,
    string? ListName,
    IReadOnlyList<string> Values)
{
    /// <summary>
    /// The server states the field is not list-backed. A fact, not an absence of evidence.
    /// </summary>
    internal static readonly FieldValueConstraint Unconstrained =
        new(FieldValueConstraintKind.Unconstrained, null, []);

    /// <summary>
    /// The picklist source could not be read for this field.
    /// </summary>
    /// <remarks>
    /// 🔴 Never rendered as <see cref="Unconstrained"/>. "We could not ask" and "the server
    /// says anything goes" are different claims, and only one of them is safe to act on.
    /// </remarks>
    internal static readonly FieldValueConstraint Unknown =
        new(FieldValueConstraintKind.Unknown, null, []);

    /// <summary>
    /// The field is backed by <paramref name="listName"/>, whose contents are
    /// <paramref name="values"/>, and the server ENFORCES that list.
    /// </summary>
    /// <remarks>
    /// Values are taken as given and sorted by the ASSEMBLER, not here — the assembler is the
    /// single ordering authority, and a second one would make byte-stability depend on two
    /// places agreeing forever.
    /// </remarks>
    internal static FieldValueConstraint ConstrainedTo(
        string? listName,
        IReadOnlyList<string> values)
        => new(FieldValueConstraintKind.ListConstrained, listName, values);

    /// <summary>
    /// A picklist offers values for <paramref name="listName"/>, but the server does not
    /// enforce them.
    /// </summary>
    /// <remarks>
    /// 🔴 Never reported as <see cref="ConstrainedTo"/>. ADO's <c>isPicklistSuggested</c> marks
    /// a list the editor offers while the server still accepts any value — so calling it a
    /// constraint would tell a caller its write must come from the list when it need not,
    /// which is this type's own overstatement failure arriving through an unread flag rather
    /// than a bad guess.
    /// </remarks>
    internal static FieldValueConstraint SuggestedFrom(
        string? listName,
        IReadOnlyList<string> values)
        => new(FieldValueConstraintKind.ListSuggested, listName, values);
}

/// <summary>The four ways a field can stand with respect to a value list.</summary>
/// <remarks>
/// 🔴 Deliberately four values and not a nullable boolean. A boolean would let a consumer write
/// <c>isPicklist == true</c> and silently treat the unreadable case as unconstrained, and it
/// could not express the SUGGESTED case at all — a list the editor offers but the server does
/// not enforce. Both collapses are the exact defect AB#237 exists to remove.
/// <para>
/// Ordered weakest-claim-first, and the numbering is load-bearing: it is a sort key in the
/// assembler's tiebreak chain, so inserting a member renumbers the document's field order.
/// </para>
/// </remarks>
internal enum FieldValueConstraintKind
{
    /// <summary>The picklist source could not be read. Not a claim about the field.</summary>
    Unknown = 0,

    /// <summary>The server states the field is not list-backed.</summary>
    Unconstrained = 1,

    /// <summary>
    /// A picklist offers values, but the server accepts anything.
    /// </summary>
    /// <remarks>
    /// 🔴 NOT a constraint. ADO's <c>isPicklistSuggested</c>: the editor shows the list, the
    /// server enforces nothing. Reporting it as <see cref="ListConstrained"/> would overstate
    /// what the process demands.
    /// </remarks>
    ListSuggested = 2,

    /// <summary>The field's value must come from a picklist, and the server enforces it.</summary>
    ListConstrained = 3,
}
