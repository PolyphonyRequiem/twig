namespace Twig.Domain.ValueObjects;

/// <summary>
/// A work item type's form layout as the DESCRIPTION carries it: pages, columns, groups and
/// controls, each with the server's explicit ordering key.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Deliberately distinct from <see cref="FormLayout"/>, and not a duplicate for its own
/// sake.</b> Two reasons, and the first is the load-bearing one:
/// </para>
/// <list type="number">
/// <item><description>
/// 🔴 <b><see cref="FormLayout"/> does not carry the server's <c>order</c> key, and this
/// document cannot be byte-stable without it.</b> That type was shaped for the TUI's detail
/// projection, which renders whatever sequence the fetch layer produced; this document must
/// produce byte-identical output across two runs, so it needs an explicit ordinal to sort on
/// rather than trusting the order an array happened to arrive in. Adding the key to
/// <see cref="FormLayout"/> instead would change a shipped PUBLIC record's constructor — a
/// breaking API change to buy a property only this document needs.
/// </description></item>
/// <item><description>
/// It is the same fetch-shape-versus-document-shape split already applied to
/// <see cref="ProcessTypeField"/> → <see cref="ProcessDescriptionField"/> and
/// <see cref="ProcessRule"/> → <see cref="ProcessDescriptionRule"/>. The description's types
/// are the ORDERED projection; nothing reaches the document without passing through the
/// assembler's sort.
/// </description></item>
/// </list>
/// <para>
/// 🔴 <b>Arrangement IS the content here, which makes this the one collection in the document
/// that must NOT be sorted alphabetically.</b> "Description sits above Acceptance Criteria" is
/// precisely the fact a reader asked for; ordering controls by label would destroy it while
/// looking tidy. So the assembler sorts on <c>Order</c> — the server's own explicit
/// arrangement key — with id as a total tiebreak, which is both faithful to the form and
/// stable across runs. Trusting array order instead would be neither provable nor testable.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Solution S2, Implementation Decision 4 ("Form layout").
/// </para>
/// </remarks>
/// <param name="Pages">The form's tabs, sorted by order then id.</param>
/// <param name="SystemControls">
/// 🔴 The form's SYSTEM controls — the server-placed controls (state, reason, assigned-to, area
/// and iteration path, tags…) that sit outside the page structure.
/// <para>
/// Carried rather than dropped, and that is the carry-everything ruling applied to a member it
/// would have been easy to overlook: the server returns <c>systemControls</c> in the same
/// response as <c>pages</c>, so it is reachable, and a process that hid or made one read-only
/// differs from one that did not. Deserializing it and then discarding it would have been an
/// omission with no marker — the exact failure S3 bans — made worse by the document's header
/// simultaneously claiming it makes no reservations.
/// </para>
/// </param>
internal sealed record ProcessDescriptionLayout(
    IReadOnlyList<ProcessDescriptionLayoutPage> Pages,
    IReadOnlyList<ProcessDescriptionLayoutControl> SystemControls);

/// <summary>One tab of the form.</summary>
/// <param name="Id">The page's stable id, e.g. <c>Basic.Epic.Epic</c>. The identity.</param>
/// <param name="Label">The tab's caption, for the reader.</param>
/// <param name="PageType">
/// <c>custom</c>, <c>history</c>, <c>links</c>, or <c>attachments</c>, verbatim. Only
/// <c>custom</c> pages carry field controls; the rest are server-rendered surfaces whose
/// content does not come from this layout. Carried anyway — a process that removed the links
/// tab differs from one that did not, and that difference must not diff clean.
/// </param>
/// <param name="Visible">Whether the page is shown.</param>
/// <param name="Inherited">
/// Whether the page came with the parent process rather than being authored here. The
/// layout's own inherited-vs-authored marking, the same distinction rules and types carry.
/// </param>
/// <param name="IsContribution">Whether the page is an extension's contribution.</param>
/// <param name="Order">
/// The server's arrangement key. 🔴 The SORT key, not decoration — see the type remarks.
/// <c>null</c> when the server omits it, which sorts before any stated order.
/// </param>
/// <param name="Sections">The page's columns, sorted by id ordinal.</param>
internal sealed record ProcessDescriptionLayoutPage(
    string Id,
    string Label,
    string PageType,
    bool Visible,
    bool Inherited,
    bool IsContribution,
    int? Order,
    IReadOnlyList<ProcessDescriptionLayoutSection> Sections);

/// <summary>
/// A column within a tab. Unlabelled by design — ADO gives sections an id (<c>Section1</c>,
/// <c>Section2</c>) but no display name, because the only thing a column expresses is
/// horizontal placement.
/// </summary>
/// <remarks>
/// 🔴 Columns are CARRIED rather than collapsed into one list, even though a diff reader has
/// no columns. Merging them is a rendering decision and a renderer can always concatenate;
/// a parse that discards them leaves no way back, and "these two groups sit side by side" is
/// a real structural difference between two processes.
/// <para>
/// Sorted by id rather than by an order key because the server gives sections no order — the
/// id itself (<c>Section1</c>…<c>Section4</c>) is the arrangement.
/// </para>
/// </remarks>
internal sealed record ProcessDescriptionLayoutSection(
    string Id,
    IReadOnlyList<ProcessDescriptionLayoutGroup> Groups);

/// <summary>A labelled box of controls within a column.</summary>
/// <param name="Id">The group's stable id. The identity.</param>
/// <param name="Label">The box's caption.</param>
/// <param name="Visible">Whether the group is shown.</param>
/// <param name="Inherited">Whether the group came with the parent process.</param>
/// <param name="IsContribution">Whether the group is an extension's contribution.</param>
/// <param name="Order">The server's arrangement key within the column. The sort key.</param>
/// <param name="Controls">The group's controls, sorted by order then id.</param>
internal sealed record ProcessDescriptionLayoutGroup(
    string Id,
    string Label,
    bool Visible,
    bool Inherited,
    bool IsContribution,
    int? Order,
    IReadOnlyList<ProcessDescriptionLayoutControl> Controls);

/// <summary>One control in a box.</summary>
/// <remarks>
/// 🔴 <see cref="ControlType"/> is carried VERBATIM. The document describes arrangement and
/// never decides presentation — a reader comparing two processes needs the server's own word
/// (<c>FieldControl</c>, <c>HtmlFieldControl</c>) rather than Twig's paraphrase of it, and a
/// control type Twig has never seen must still show up in the diff.
/// </remarks>
/// <param name="Id">
/// For an ordinary field control this is the field REFERENCE name — which is what lets a
/// reader tie the layout back to the type's field list. For a contribution it is the
/// contribution id and names no field.
/// </param>
/// <param name="Label">The control's caption.</param>
/// <param name="ControlType">The server's control kind, verbatim.</param>
/// <param name="ReadOnly">Whether the form presents the control read-only.</param>
/// <param name="Visible">Whether the control is shown.</param>
/// <param name="Inherited">Whether the control came with the parent process.</param>
/// <param name="IsContribution">Whether the control is an extension's contribution.</param>
/// <param name="Order">The server's arrangement key within the group. The sort key.</param>
internal sealed record ProcessDescriptionLayoutControl(
    string Id,
    string Label,
    string ControlType,
    bool ReadOnly,
    bool Visible,
    bool Inherited,
    bool IsContribution,
    int? Order);
