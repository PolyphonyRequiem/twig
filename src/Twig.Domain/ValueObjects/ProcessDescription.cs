namespace Twig.Domain.ValueObjects;

/// <summary>
/// The document <c>twig process description</c> emits: a structural description of an ADO
/// process, written to a file so an ordinary diff tool can compare two of them.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Byte-stability is a hard requirement, not a quality goal.</b> Two runs against an
/// unchanged process must produce byte-identical documents, with
/// <see cref="ProcessDescriptionHeader.CapturedAtUtc"/> the ONLY permitted variance. That
/// is what makes an ordinary diff the right comparator instead of a bespoke one: if
/// ordering wobbles, the diff fills with noise and the artifact is worthless.
/// </para>
/// <para>
/// The shape below is built to make that provable rather than hoped for. <b>Every
/// collection is an ordered <see cref="IReadOnlyList{T}"/> sorted by the assembler on an
/// ORDINAL key</b> — never a dictionary, never a set, never a collection whose order came
/// from the server or from the order concurrent fetches happened to complete in. The
/// assembler sorts once, at construction, and the renderer walks in the order it is given.
/// A future contributor adding a member here must give it a deliberate ordering; leaving
/// that to the wire order is how this silently regresses.
/// </para>
/// <para>
/// 🔴 <b>Structure only — never work item values.</b> The document describes how a process
/// is BUILT (types, fields, states, transitions). It never contains anyone's actual work.
/// That is precisely what makes the file safe to hand to someone outside the team.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Solution S2 and S4,
/// Implementation Decisions 4 and 9. This type is <c>internal</c> deliberately (Decision
/// 9): the FILE is the only public promise, and it carries its own version number.
/// </para>
/// </remarks>
/// <param name="Header">Provenance — what this document describes, and when.</param>
/// <param name="Types">
/// The described types, sorted by <see cref="ProcessDescriptionType.ReferenceName"/>
/// ordinal. Reference name and not display name because display names lie: one process was
/// observed using reference names from an entirely differently-named process.
/// </param>
internal sealed record ProcessDescription(
    ProcessDescriptionHeader Header,
    IReadOnlyList<ProcessDescriptionType> Types);

/// <summary>
/// The document header: what process this describes, when it was taken, and under what
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <see cref="CapturedAtUtc"/> is the only part of the document permitted to vary
/// between two runs against an unchanged process, and it lives HERE — in a header a diff
/// tool can be pointed past — rather than interleaved into the body. Omitting it entirely
/// was considered and rejected: it would buy exact byte-identity at the cost of the reader
/// knowing WHEN the claim was true, and a description whose age is unknowable is a weaker
/// truth claim than one with a single varying line.
/// </para>
/// <para>
/// 🔴 <see cref="RouteApiVersions"/> is not decoration. On this route family the
/// api-version selects the response SCHEMA, not merely the route version — the same
/// per-type fields URL returns disjoint attribute sets at two neighbouring preview
/// versions. Recording the pinned version per route is what stops two documents taken
/// months apart differing merely because the server moved.
/// </para>
/// </remarks>
/// <param name="Organization">The ADO organization URL the description was taken from.</param>
/// <param name="ProjectName">
/// The project whose process this is. 🔴 Recorded because the process was resolved BY ID
/// VIA THIS PROJECT, never by process name — the project named "Twig" does not run on the
/// process named "Twig", and resolving by name silently describes the wrong process.
/// </param>
/// <param name="ProcessId">
/// The process template id. This, not the name, is what the description is a truth claim
/// about.
/// </param>
/// <param name="ProcessName">
/// The process's display name where known, for the reader's orientation only. Never used
/// to resolve anything.
/// </param>
/// <param name="CapturedAtUtc">When the capture ran. The only permitted variance.</param>
/// <param name="DescriptorVersion">
/// The document's own version, starting at <c>0.1</c>. Shape changes are DECLARED in the
/// artifact rather than discovered by whoever they broke. 0.1 and not 1.0 because
/// components are still under design — going up costs nothing, coming down costs
/// credibility.
/// </param>
/// <param name="RouteApiVersions">
/// The pinned remote api-version per route actually used to build this document, sorted by
/// route key ordinal.
/// </param>
/// <param name="KnownGaps">
/// 🔴 What this document is NOT yet trustworthy about, sorted by subject ordinal. Empty
/// means the document makes no reservations. A gap here is a promise the document
/// deliberately declines to make; see <see cref="ProcessDescriptionGap"/>.
/// </param>
internal sealed record ProcessDescriptionHeader(
    string Organization,
    string ProjectName,
    string ProcessId,
    string ProcessName,
    DateTimeOffset CapturedAtUtc,
    string DescriptorVersion,
    IReadOnlyList<ProcessDescriptionRouteVersion> RouteApiVersions,
    IReadOnlyList<ProcessDescriptionGap> KnownGaps)
{
    /// <summary>
    /// The descriptor version this build emits. 🔴 Bump this when the document's SHAPE
    /// changes, and say so in the changelog — that announcement is the entire reason the
    /// artifact carries a version at all.
    /// </summary>
    internal const string CurrentDescriptorVersion = "0.1";
}

/// <summary>One route and the api-version pinned for it.</summary>
/// <param name="Route">
/// A stable, human-readable route key (e.g. <c>work/processes/{id}/workItemTypes</c>).
/// Ordinal-sorted in the header so two documents line up.
/// </param>
/// <param name="ApiVersion">The pinned version, verbatim.</param>
internal sealed record ProcessDescriptionRouteVersion(string Route, string ApiVersion);

/// <summary>
/// A declared incompleteness: something this document does NOT yet claim to be right about.
/// </summary>
/// <remarks>
/// 🔴 This exists so an incomplete document cannot masquerade as a complete one. The same
/// reasoning that makes the abridged text rendering declare itself applies here: a
/// document that is silently wrong about a property is strictly worse than one that says
/// which property it declines to answer. A reader diffing two documents can see the
/// reservation in both.
/// </remarks>
/// <param name="Subject">What the gap is about (e.g. <c>conditionalRequiredness</c>).</param>
/// <param name="Detail">One sentence a human can act on.</param>
/// <param name="TrackedIn">The work item that closes the gap, e.g. <c>AB#236</c>.</param>
internal sealed record ProcessDescriptionGap(string Subject, string Detail, string TrackedIn);

/// <summary>One work item type as the description carries it.</summary>
/// <param name="ReferenceName">
/// The type's stable identity. 🔴 This is what two processes are matched by; display names
/// lie.
/// </param>
/// <param name="Name">The display name, for the reader. Never matched on.</param>
/// <param name="Description">The type's description, or empty when the server sends none.</param>
/// <param name="Customization">
/// Whether the type is authored here or inherited: <c>custom</c>, <c>inherited</c>, or
/// <c>system</c>, carried verbatim from the server. Twig does not reinterpret the server's
/// vocabulary.
/// </param>
/// <param name="Inherits">
/// The parent type's reference name when this type derives from one, else <c>null</c>.
/// </param>
/// <param name="IsDisabled">Whether the process has disabled the type.</param>
/// <param name="Fields">
/// The type's fields, sorted by reference name ordinal. 🔴 TYPE-SCOPED — not the
/// project-wide field list, which is identical for every type and is the founding
/// correctness defect this feature exists to fix.
/// </param>
/// <param name="States">The type's states, in server order then name.</param>
/// <param name="Transitions">
/// The allowed state transitions, sorted by from-state then to-state ordinal.
/// </param>
/// <param name="Unfetched">
/// 🔴 The parts of this type that could NOT be fetched, sorted ordinal. Empty when
/// everything was read successfully.
/// <para>
/// This exists because an empty collection is otherwise ambiguous in the one direction
/// that matters: "this type genuinely has no fields" and "we failed to ask" render
/// identically, and the second silently understates the process. A reader diffing two
/// documents would see a clean diff where one side simply failed. Naming the failure keeps
/// the document honest about the limits of its own knowledge, the same way the header's
/// known gaps do.
/// </para>
/// </param>
internal sealed record ProcessDescriptionType(
    string ReferenceName,
    string Name,
    string Description,
    string Customization,
    string? Inherits,
    bool IsDisabled,
    IReadOnlyList<ProcessDescriptionField> Fields,
    IReadOnlyList<ProcessTypeState> States,
    IReadOnlyList<ProcessTypeTransition> Transitions,
    IReadOnlyList<string> Unfetched);

/// <summary>
/// One field as the DOCUMENT carries it: the per-type fields route's structural facts, plus
/// requiredness merged from the rules route.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This type exists so the merge cannot be skipped by accident.</b>
/// <see cref="ProcessTypeField"/> is the fetch layer's honest report of ONE source, and its
/// <see cref="ProcessTypeField.RequiredUnconditionally"/> is named for exactly what that
/// source knows. The document must not carry that property directly: the per-type fields
/// route reports unconditional requiredness only, so a field made mandatory by a rule —
/// <i>when State = Done → makeRequired</i> — reads there as not-required, which is wrong
/// about exactly the fields a caller most needs and wrong in the silent direction.
/// </para>
/// <para>
/// So the document carries <see cref="Requiredness"/>, which can express the conditional
/// case, and there is no boolean on this type for a renderer to reach for instead.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Implementation Decision 5(a).
/// </para>
/// </remarks>
/// <param name="ReferenceName">The field's stable identity. Display names lie.</param>
/// <param name="Name">The display name the web editor shows.</param>
/// <param name="Type">The data type (<c>string</c>, <c>integer</c>, <c>html</c>, …).</param>
/// <param name="DefaultValue">The value the server pre-fills, or <c>null</c>.</param>
/// <param name="Requiredness">
/// 🔴 The MERGED answer: unconditional, conditional-with-its-conditions, or never. Not a
/// boolean, because a boolean cannot carry the conditional case without lying.
/// </param>
/// <param name="ValueConstraint">
/// 🔴 Whether the field's value is restricted to a list, and to WHICH values — read as an
/// explicit server fact, never guessed from the field's name or type. The mirror of
/// <paramref name="Requiredness"/>: that one could understate what a process demands, this
/// one could OVERSTATE it.
/// </param>
/// <param name="Customization">
/// <c>custom</c> | <c>inherited</c> | <c>system</c>, carried verbatim from the server.
/// </param>
/// <param name="IsLocked">Whether the field is locked against edits by the process.</param>
/// <param name="Description">The field's description, or empty when the server sends none.</param>
internal sealed record ProcessDescriptionField(
    string ReferenceName,
    string Name,
    string Type,
    string? DefaultValue,
    FieldRequiredness Requiredness,
    FieldValueConstraint ValueConstraint,
    string Customization,
    bool IsLocked,
    string Description);
