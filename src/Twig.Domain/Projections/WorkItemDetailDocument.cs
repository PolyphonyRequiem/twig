namespace Twig.Domain.Projections;

/// <summary>
/// The framework-neutral detail document a read-only host receives for one work item:
/// the server's page → section → group → control structure with each field control's
/// value resolved against the item.
/// </summary>
/// <remarks>
/// <para>
/// <b>Governing rule: carry every fact the source gave us; let the host decide what to
/// drop.</b> Nothing here is filtered on visibility, page type, contribution status, or
/// value presence — a projection that discards a fact leaves no way back.
/// </para>
/// <para>
/// Constructed from an already-materialized <see cref="ValueObjects.FormLayout"/> plus
/// <see cref="ValueObjects.WorkItemSnapshot"/>. No provider, no async, no store, no DI,
/// no renderer type. <see cref="ValueObjects.WorkItemTypeAppearance"/> travels
/// SEPARATELY — it is Twig's look-and-feel opinion, not the server's structure.
/// </para>
/// </remarks>
public sealed record WorkItemDetailDocument(
    int WorkItemId,
    int Revision,
    string WorkItemTypeReferenceName,
    string ProcessId,
    IReadOnlyList<DetailPage> Pages);

/// <summary>A tab in the form.</summary>
/// <param name="PageType">
/// <c>custom</c>, <c>history</c>, <c>links</c>, or <c>attachments</c>, verbatim from the
/// server. Non-<c>custom</c> pages are carried FLAGGED, not filtered: their content is
/// server-rendered and this layout does not supply it, but a host may still want to show
/// a disabled <i>History</i> tab.
/// </param>
public sealed record DetailPage(
    string Id,
    string Label,
    string PageType,
    bool Visible,
    bool IsContribution,
    IReadOnlyList<DetailSection> Sections)
{
    /// <summary>
    /// <c>true</c> when <see cref="PageType"/> is <c>custom</c> — the only page type whose
    /// controls carry fields. Convenience over the verbatim string, not a replacement for it.
    /// </summary>
    public bool CarriesFieldControls =>
        string.Equals(PageType, "custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every group across every column, in column-then-order sequence.</summary>
    public IEnumerable<DetailGroup> AllGroups => Sections.SelectMany(section => section.Groups);
}

/// <summary>A column within a tab. Unlabelled by design; merging is the host's call.</summary>
public sealed record DetailSection(
    string Id,
    IReadOnlyList<DetailGroup> Groups);

/// <summary>A labelled box of controls within a column.</summary>
public sealed record DetailGroup(
    string Id,
    string Label,
    bool Visible,
    bool IsContribution,
    IReadOnlyList<DetailControl> Controls);

/// <summary>
/// One control in a box, with its value resolved.
/// </summary>
/// <param name="Id">Field reference name for a field control; the contribution id otherwise.</param>
/// <param name="ControlType">
/// The server's control-type string, carried verbatim. There is deliberately no closed
/// Twig-owned widget enum: process customization means the set is open, and an
/// <c>Other</c> bucket would discard the name.
/// </param>
/// <param name="ReadOnly">Reported as the server set it. Never enforced, and never an editing contract.</param>
/// <param name="Visible">Reported as the server set it. Absent on the wire means visible.</param>
/// <param name="Value">
/// <c>null</c> for a contribution control, which names no field. Otherwise one of the
/// three <see cref="DetailFieldState"/> states.
/// </param>
public sealed record DetailControl(
    string Id,
    string Label,
    string ControlType,
    bool ReadOnly,
    bool Visible,
    bool IsContribution,
    DetailFieldValue? Value);

/// <summary>
/// Which of the three states a field control's value is in.
/// </summary>
/// <remarks>
/// Two states were rejected. A host that naively looks each control's field up in
/// <c>WorkItemSnapshot.Fields</c> gets nothing for a large, type-dependent slice of every
/// form — <c>FieldImportFilter</c> excludes all eight core fields, every boolean, and
/// unlisted read-only fields — and cannot distinguish that from a genuinely blank field.
/// </remarks>
public enum DetailFieldState
{
    /// <summary>The item has a value here and this document carries it in full.</summary>
    HasValue = 0,

    /// <summary>The item genuinely has no value here.</summary>
    EmptyOnServer = 1,

    /// <summary>Twig's projection does not transport this field at all.</summary>
    NotCarriedByTwig = 2,
}

/// <summary>
/// A resolved field value: its state, the complete source value, and an optional
/// Twig-computed short form.
/// </summary>
/// <param name="State">Which of the three states this field is in.</param>
/// <param name="Full">
/// The complete source value, NEVER truncated. <c>null</c> unless
/// <see cref="State"/> is <see cref="DetailFieldState.HasValue"/>.
/// </param>
/// <param name="Short">
/// A Twig-computed one-line summary, present only when it differs from
/// <see cref="Full"/>. Hosts get one consistent cut instead of each inventing its own;
/// <see cref="Full"/> remains available so an expanded view is always possible.
/// </param>
public sealed record DetailFieldValue(
    DetailFieldState State,
    string? Full,
    string? Short)
{
    /// <summary><c>true</c> when a short form was computed and differs from the full value.</summary>
    public bool IsAbbreviated => Short is not null;
}
