using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Process;

/// <summary>
/// Assembles a <see cref="ProcessDescription"/> from an
/// <see cref="IProcessDescriptionSource"/>. The ONE seam the CLI and the agent surface both
/// go through, so there is exactly one document format rather than two that drift.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This class is the single place ordering is decided, and that is the whole point.</b>
/// Byte-stability is a hard requirement: two runs against an unchanged process must produce
/// byte-identical documents, the header's capture timestamp excepted. Ordering that came
/// from the server, from a dictionary's iteration, or from the order concurrent fetches
/// happened to finish in is not stable, so every collection this class emits is sorted here
/// on an explicit ORDINAL key.
/// </para>
/// <para>
/// Ordinal and not culture-aware: a culture-sensitive comparison can order the same two
/// strings differently on two machines or two .NET versions, which would make a document
/// taken on a contributor's laptop diff dirty against one taken in CI for no real reason.
/// </para>
/// <para>
/// 🔴 <b>Concurrency does not reach the ordering.</b> The whole-process path fetches types
/// concurrently — a ruled mitigation, since ~32 serial round-trips is ~20 s — but results
/// are re-sorted after the gather rather than appended as they complete. A test may drive
/// <see cref="IProcessDescriptionSource.GetTypeDetailAsync"/> to complete in exactly
/// reversed order and the document must be byte-identical; that assertion is real precisely
/// because nothing downstream of the gather depends on completion order.
/// </para>
/// <para>
/// 🔴 <b>The assembler declares what the document is not yet trustworthy about.</b> See
/// <see cref="KnownGaps"/>. At descriptor version 0.1 the document is KNOWN INCOMPLETE
/// about conditional requiredness and picklist values, and it says so on its face rather
/// than presenting a partial truth as a whole one.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> — the seam section,
/// Solution S2 and S4, Implementation Decisions 3, 4, 9, 11.
/// </para>
/// </remarks>
internal sealed class ProcessDescriptionAssembler(IProcessDescriptionSource source)
{
    /// <summary>
    /// 🔴 The reservations this descriptor version makes about its own trustworthiness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are real, verified, and deliberately out of scope at 0.1 — they are separate
    /// tickets. Declaring them is not an apology, it is the honest form of shipping an
    /// incomplete document: a reader diffing two descriptions sees the same reservation in
    /// both and knows not to trust those two properties yet.
    /// </para>
    /// <para>
    /// 🔴 <b>Remove an entry only when the corresponding ticket actually lands.</b> Deleting
    /// one early converts a document that is honestly incomplete into one that is silently
    /// wrong, which is strictly worse and is the exact failure class this feature exists to
    /// prevent.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyList<ProcessDescriptionGap> KnownGaps =
    [
        new ProcessDescriptionGap(
            "conditionalRequiredness",
            "A field's 'required' flag reports UNCONDITIONAL requiredness only. A field made "
                + "mandatory by a rule (when State = Done -> makeRequired) reads as not-required "
                + "here. Do not trust this document about requiredness yet.",
            "AB#236"),
        new ProcessDescriptionGap(
            "picklistValues",
            "Fields are not yet reported as list-constrained or unconstrained, and no accepted "
                + "values are resolved. A field that looks like a choice list may or may not be "
                + "one. Do not trust this document about accepted values yet.",
            "AB#237"),
    ];

    /// <summary>
    /// The routes this assembler's document is built from, and the api-version pinned for
    /// each. Recorded in the header so two documents taken months apart cannot differ merely
    /// because the server moved.
    /// </summary>
    /// <remarks>
    /// Supplied by the fetch layer rather than hardcoded here: the domain layer must not
    /// know infrastructure's route strings, and a version that drifted from the one actually
    /// called would be worse than no header line at all.
    /// </remarks>
    internal IReadOnlyList<ProcessDescriptionRouteVersion> RouteVersions { get; init; } = [];

    /// <summary>
    /// Assembles the description.
    /// </summary>
    /// <param name="typeReferenceNames">
    /// The types to describe, by REFERENCE name. Pass <c>null</c> or an empty list to
    /// describe every type in the process.
    /// <para>
    /// 🔴 Selection is only ever WHICH TYPES, never which parts of a type. Per-part selection
    /// is a filter, and a filtered document lets a real difference hide in the part that was
    /// dropped — with the reader unable to tell anything was.
    /// </para>
    /// </param>
    /// <param name="capturedAtUtc">
    /// The capture timestamp, injected rather than read from the clock so a test can hold it
    /// fixed and assert the REST of the document is byte-identical. That is the only way to
    /// test byte-stability without asserting against a moving target.
    /// </param>
    /// <returns>
    /// The assembled description, or <c>null</c> when the process could not be resolved or
    /// its type list could not be fetched. Never a partial document standing in for a failed
    /// fetch.
    /// </returns>
    /// <exception cref="ProcessDescriptionTypeNotFoundException">
    /// A named type does not exist in the process. A hard error and not an empty document:
    /// silently describing nothing when the caller named a type is how a script banks a
    /// wrong answer.
    /// </exception>
    public async Task<ProcessDescription?> AssembleAsync(
        IReadOnlyList<string>? typeReferenceNames,
        DateTimeOffset capturedAtUtc,
        CancellationToken ct = default)
    {
        var identity = await source.GetProcessIdentityAsync(ct);
        if (identity is null)
            return null;

        var available = await source.GetTypesAsync(ct);
        if (available is null)
            return null;

        var selected = SelectTypes(available, typeReferenceNames);

        // 🔴 Fetched concurrently — the ruled latency mitigation. Ordering is NOT taken from
        // completion: results are indexed back onto `selected` positionally, then sorted
        // below. Task.WhenAll preserves input order in its result array regardless of which
        // task finished first, which is what makes the reverse-completion test meaningful
        // rather than accidental.
        var details = await Task.WhenAll(
            selected.Select(type => source.GetTypeDetailAsync(
                type.ReferenceName,
                // 🔴 Passed through, not dropped. A DERIVED type is keyed by its own
                // reference name on the process routes but by its PARENT's on the route that
                // carries transitions; without this the fetch silently returns zero
                // transitions for exactly the derived types.
                type.Inherits,
                ct)));

        var described = new List<ProcessDescriptionType>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var summary = selected[i];
            var detail = details[i];

            described.Add(new ProcessDescriptionType(
                summary.ReferenceName,
                summary.Name,
                summary.Description,
                summary.Customization,
                summary.Inherits,
                summary.IsDisabled,
                // A type whose detail could not be fetched carries empty collections rather
                // than dropping out of the document: its ABSENCE would read as "this process
                // does not have this type", which is a different and wrong claim. The type's
                // identity is known from the list call and is still true.
                SortFields(detail?.Fields),
                SortStates(detail?.States),
                SortTransitions(detail?.Transitions),
                // 🔴 …but the emptiness is LABELLED. Without this, a type whose fetch failed
                // is byte-identical to one that genuinely has nothing, and a reader diffing
                // two documents sees a clean diff where one side simply failed to ask. When
                // the whole detail call failed, every part is unfetched.
                detail is null
                    ? WholeTypeUnfetched
                    : SortUnfetched(detail.Unfetched)));
        }

        // The one ordering that decides whether two whole-process documents line up.
        // 🔴 List<T>.Sort is UNSTABLE, so a tie would order by an unspecified rule rather than
        // falling back to input order. Reference names are unique within a process, so the
        // name comparison alone is total in practice — the display-name tiebreak makes that
        // independent of the assumption rather than load-bearing on it.
        described.Sort(static (left, right) =>
        {
            var byReference = string.CompareOrdinal(left.ReferenceName, right.ReferenceName);
            return byReference != 0
                ? byReference
                : string.CompareOrdinal(left.Name, right.Name);
        });

        var header = new ProcessDescriptionHeader(
            identity.Organization,
            identity.ProjectName,
            identity.ProcessId,
            identity.ProcessName,
            capturedAtUtc,
            ProcessDescriptionHeader.CurrentDescriptorVersion,
            [.. RouteVersions.OrderBy(static r => r.Route, StringComparer.Ordinal)],
            [.. KnownGaps.OrderBy(static g => g.Subject, StringComparer.Ordinal)]);

        return new ProcessDescription(header, described);
    }

    /// <remarks>
    /// Selection matches on REFERENCE name and is ordinal-case-insensitive only so a caller
    /// typing a reference name with different casing is served rather than told it does not
    /// exist. Matching is never on display name — that is the collision this design exists
    /// to avoid.
    /// <para>
    /// 🔴 De-duplicated. Because matching is case-INSENSITIVE, <c>["Niflheim.Task",
    /// "niflheim.task"]</c> would otherwise resolve to the same type twice, fetch it twice,
    /// and emit it twice — producing a document that claims the process contains the type
    /// twice. The sort by reference name puts the copies adjacent, which makes it look like
    /// a real duplicate in the process rather than a caller artefact.
    /// </para>
    /// </remarks>
    private static List<ProcessTypeSummary> SelectTypes(
        IReadOnlyList<ProcessTypeSummary> available,
        IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            return [.. available];

        var selected = new List<ProcessTypeSummary>(requested.Count);
        var alreadySelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wanted in requested)
        {
            var match = available.FirstOrDefault(type => string.Equals(
                type.ReferenceName, wanted, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                throw new ProcessDescriptionTypeNotFoundException(wanted);

            // Keyed on the RESOLVED reference name, not the caller's spelling, so casing
            // variants of the same type collapse to one entry.
            if (alreadySelected.Add(match.ReferenceName))
                selected.Add(match);
        }

        return selected;
    }

    // ── Ordering ──────────────────────────────────────────────────────────────
    // Each sort below is deliberate and ordinal. Adding a member to the document without
    // giving it one of these is how byte-stability silently regresses.

    /// <remarks>
    /// Every part of a type, for the case where the whole detail fetch failed. Ordinal-sorted
    /// like every other collection so it cannot perturb byte-stability.
    /// </remarks>
    private static readonly IReadOnlyList<string> WholeTypeUnfetched =
        ["fields", "states", "transitions"];

    /// <remarks>
    /// Sorted and de-duplicated so two documents cannot differ merely in the order a fetch
    /// layer happened to report its failures.
    /// </remarks>
    private static IReadOnlyList<string> SortUnfetched(IReadOnlyList<string>? unfetched)
        => unfetched is null || unfetched.Count == 0
            ? []
            : [.. unfetched.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)];

    /// <remarks>
    /// Reference name is the field's stable identity and is unique within a type in practice.
    /// The extra tiebreaks are not redundant: if the server ever returns two rows sharing a
    /// reference name, ordering would otherwise fall through <c>OrderBy</c>'s stability to
    /// WIRE order — reintroducing exactly the non-determinism this class exists to remove,
    /// and doing so silently.
    /// </remarks>
    private static IReadOnlyList<ProcessTypeField> SortFields(IReadOnlyList<ProcessTypeField>? fields)
        => fields is null
            ? []
            : [.. fields
                .OrderBy(static f => f.ReferenceName, StringComparer.Ordinal)
                .ThenBy(static f => f.Name, StringComparer.Ordinal)
                .ThenBy(static f => f.Type, StringComparer.Ordinal)];

    /// <remarks>
    /// Server order first — it is the order the web editor lays states out and is meaningful
    /// to a reader — then name, then the remaining discriminators so a tie on BOTH order and
    /// name cannot fall through to wire order.
    /// </remarks>
    private static IReadOnlyList<ProcessTypeState> SortStates(IReadOnlyList<ProcessTypeState>? states)
        => states is null
            ? []
            : [.. states
                .OrderBy(static s => s.Order)
                .ThenBy(static s => s.Name, StringComparer.Ordinal)
                .ThenBy(static s => s.StateCategory, StringComparer.Ordinal)
                .ThenBy(static s => s.Customization, StringComparer.Ordinal)];

    /// <remarks>
    /// From-state then to-state, both ordinal. The initial transition carries an EMPTY
    /// from-state and therefore sorts first — that is a guaranteed property of ordinal
    /// comparison on a prefix, not a coincidence, so a reader can rely on "what state does a
    /// new item start in" appearing before the rest.
    /// </remarks>
    private static IReadOnlyList<ProcessTypeTransition> SortTransitions(
        IReadOnlyList<ProcessTypeTransition>? transitions)
        => transitions is null
            ? []
            : [.. transitions
                .OrderBy(static t => t.FromState, StringComparer.Ordinal)
                .ThenBy(static t => t.ToState, StringComparer.Ordinal)];
}

/// <summary>
/// Thrown when a caller names a type the process does not have.
/// </summary>
/// <remarks>
/// A hard error deliberately. Rendering an empty document for a type that does not exist
/// would let a script bank a file that says "this process has nothing" when the truth is
/// "you asked for something that is not here".
/// </remarks>
internal sealed class ProcessDescriptionTypeNotFoundException(string typeReferenceName)
    : Exception($"Work item type '{typeReferenceName}' does not exist in this process.")
{
    /// <summary>The type reference name that was asked for, so the caller can be told.</summary>
    public string TypeReferenceName { get; } = typeReferenceName;
}
