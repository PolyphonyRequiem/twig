namespace Twig.Domain.ValueObjects;

/// <summary>
/// The work item form layout for one work item type, as the server defines it:
/// tabs, boxes, and ordered fields.
/// </summary>
/// <remarks>
/// <para>
/// This is the input to the server-driven editor (wayfinder-1.0 ticket 1003). The 1.0
/// editor takes its structure from here rather than from a hand-written layout, because
/// a hand-written one is wrong for every customer whose process is customized.
/// </para>
/// <para>
/// <b>Structure transfers; widgets do not.</b> This type deliberately describes only
/// arrangement — what is grouped with what, and in what order. It says nothing about how
/// any control should be drawn. <see cref="LayoutControl.ControlType"/> is carried
/// verbatim so a renderer can decide that later; some kinds (rich text, links grids,
/// attachments, history) have no obvious terminal form and that mapping is hand-written
/// work this type does not eliminate.
/// </para>
/// <para>
/// ADO nests pages → sections → groups → controls, and this shape preserves all four
/// levels. A <see cref="LayoutSection"/> is the web form's COLUMN: unlabelled, and
/// carrying no meaning beyond "these boxes sit side by side".
/// </para>
/// <para>
/// <b>Columns are kept even though a terminal is usually one column wide.</b> Collapsing
/// them here was the original design and it was wrong: merging columns is a RENDERING
/// decision, and a renderer that wants one column can always concatenate them in order.
/// A parse that discards them leaves no way back — the fact is gone before any renderer
/// gets to choose. Keep the server's structure intact; decide presentation late.
/// </para>
/// </remarks>
public sealed record FormLayout(
    string WorkItemTypeReferenceName,
    string ProcessId,
    IReadOnlyList<LayoutPage> Pages)
{
    /// <summary>
    /// The server-placed controls that sit outside the page structure — state, reason,
    /// assigned-to, area and iteration path, history, links, attachments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Added as an init-only member with a default, deliberately NOT as a fourth
    /// positional parameter.</b> This record is public and analyzer-governed, and a
    /// positional addition rewrites its constructor and its <c>Deconstruct</c>, and the type
    /// is referenced across the domain, the CLI, the TUI and the sample host. An init-only
    /// member with a default leaves
    /// every existing construction site compiling untouched, which is what makes carrying
    /// this fact affordable at all.
    /// </para>
    /// <para>
    /// 🔴 <b>Carried rather than discarded (AB#247).</b> The server returns
    /// <c>systemControls</c> in the SAME response as <c>pages</c>, so it was already being
    /// deserialized and then thrown away — an omission with no marker, while the layout
    /// command claimed to render "the form". These are the controls a person sees at the
    /// top of every work item; a form rendering without them is missing its header.
    /// </para>
    /// <para>
    /// Empty is the honest default: a layout built by a caller that has no system controls
    /// to declare (the fallback layout, and every hand-built test fixture) asserts nothing
    /// about the server's. An absent key is read as empty rather than as a distinct state —
    /// no observed layout omits it, and the parse guards the omission anyway.
    /// </para>
    /// </remarks>
    public IReadOnlyList<LayoutControl> SystemControls { get; init; } = [];
}

/// <summary>A tab in the form.</summary>
/// <param name="PageType">
/// <c>custom</c>, <c>history</c>, <c>links</c>, or <c>attachments</c>. Only
/// <c>custom</c> pages carry field controls; the other three are server-rendered
/// surfaces whose content does not come from this layout.
/// </param>
public sealed record LayoutPage(
    string Id,
    string Label,
    string PageType,
    bool Visible,
    bool IsContribution,
    IReadOnlyList<LayoutSection> Sections)
{
    /// <summary>
    /// Every group across every column, in column-then-order sequence — the single-column
    /// projection, for renderers that do not lay columns out side by side.
    /// </summary>
    /// <remarks>
    /// Convenience only. <see cref="Sections"/> remains the source of truth so a renderer
    /// that CAN place columns side by side is not blocked by this shortcut.
    /// </remarks>
    public IEnumerable<LayoutGroup> AllGroups => Sections.SelectMany(section => section.Groups);
}

/// <summary>
/// A column within a tab. Unlabelled by design — ADO gives sections an id
/// (<c>Section1</c>, <c>Section2</c>) but no display name, because the only thing a
/// column expresses is horizontal placement.
/// </summary>
public sealed record LayoutSection(
    string Id,
    IReadOnlyList<LayoutGroup> Groups);

/// <summary>A labelled box of fields within a column.</summary>
public sealed record LayoutGroup(
    string Id,
    string Label,
    bool Visible,
    bool IsContribution,
    IReadOnlyList<LayoutControl> Controls);

/// <summary>
/// One control in a box. For an ordinary field control <paramref name="Id"/> is the
/// field reference name; for a contribution it is the contribution id and names no field.
/// </summary>
public sealed record LayoutControl(
    string Id,
    string Label,
    string ControlType,
    bool ReadOnly,
    bool Visible,
    bool IsContribution);
