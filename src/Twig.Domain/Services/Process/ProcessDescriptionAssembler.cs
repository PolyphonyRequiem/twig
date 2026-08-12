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
/// <see cref="KnownGaps"/>. At descriptor version 0.1 that list is EMPTY: every reservation
/// 0.1 opened with has been closed. Conditional requiredness went with AB#236's rules
/// merge, and picklist values with AB#237's constraint merge. The list stays in the shape
/// and in the header — an empty reservation list is itself a claim ("this document makes no
/// reservations"), and it is the claim a reader needs in order to read a future non-empty
/// one as meaningful.
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
    /// 🔴 <b>An entry goes here for every Decision 4 content item the document does not yet
    /// carry — not merely for the ones a ticket happened to name.</b> AB#237 emptied this list
    /// by closing the last of 0.1's two original reservations, and that was a mistake caught in
    /// review: it converted three EXISTING silent omissions (rules, behaviour membership, form
    /// layout — all required by Decision 4, all still unshipped and tracked in AB#238) into an
    /// affirmative claim that the document omits nothing. A missing reservation is as much a
    /// lie as a false one, and the affirmative version is worse than the silence it replaced.
    /// </para>
    /// <para>
    /// So the two CLOSED reservations are gone — conditional requiredness with AB#236's rules
    /// merge, picklist values with AB#237's constraint merge, and declaring either now would
    /// warn a reader off an answer this document does give — while the three genuinely-absent
    /// content items are declared. That is the honest state: this document is trustworthy about
    /// what it carries and says plainly what it does not carry yet.
    /// </para>
    /// <para>
    /// 🔴 <b>Remove an entry only when the corresponding ticket actually lands, and add one for
    /// any content item that ships incomplete.</b> Deleting one early converts a document that
    /// is honestly incomplete into one that is silently wrong — the exact failure class this
    /// feature exists to prevent.
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlyList<ProcessDescriptionGap> KnownGaps =
    [
        new ProcessDescriptionGap(
            "behaviourMembership",
            "Which backlog levels a type belongs to is not reported. A reader cannot tell from "
                + "this document whether a type appears on a portfolio, requirement or task "
                + "backlog.",
            "AB#238"),
        new ProcessDescriptionGap(
            "formLayout",
            "The work item form's layout — pages, sections, groups and controls — is not "
                + "reported. Two processes whose forms differ can produce identical documents.",
            "AB#238"),
        new ProcessDescriptionGap(
            "rules",
            "Rules are read for their makeRequired actions but are not themselves reported, so "
                + "a rule that sets a value, copies a field or hides one is invisible here. "
                + "Requiredness IS answered; the rest of a rule's effect is not.",
            "AB#238"),
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

        // 🔴 Fetched ONCE per run, not per type (AB#237). The picklist association is only
        // readable off the ORG-scoped field route, so it is the same answer for every type;
        // asking per type would multiply round-trips for an identical result. Started before
        // the per-type gather so it overlaps with it rather than adding to the critical path.
        var constraintsTask = source.GetFieldValueConstraintsAsync(ct);

        // 🔴 Fetched concurrently — the ruled latency mitigation. Ordering is NOT taken from
        // completion: results are indexed back onto `selected` positionally, then sorted
        // below. Task.WhenAll preserves input order in its result array regardless of which
        // task finished first, which is what makes the reverse-completion test meaningful
        // rather than accidental.
        var detailsTask = Task.WhenAll(
            selected.Select(type => source.GetTypeDetailAsync(
                type.ReferenceName,
                // 🔴 Passed through, not dropped. A DERIVED type is keyed by its own
                // reference name on the process routes but by its PARENT's on the route that
                // carries transitions; without this the fetch silently returns zero
                // transitions for exactly the derived types.
                type.Inherits,
                ct)));

        // Gathered before either is awaited individually, so a fault in one cannot leave the
        // other's exception unobserved.
        await Task.WhenAll(detailsTask, constraintsTask).ConfigureAwait(false);

        var details = await detailsTask;
        var constraints = await constraintsTask;

        var described = new List<ProcessDescriptionType>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var summary = selected[i];
            var detail = details[i];

            // Hoisted so the unfetched label below can be derived from the RESOLVED answers
            // rather than from the constraint map's nullness — see PicklistUnfetched.
            var mergedFields = MergeFields(detail?.Fields, detail?.Rules, constraints);

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
                //
                // 🔴 Fields are MERGED with the rules source here, not carried through from
                // the fields source. AB#236: the fields route reports unconditional
                // requiredness only, so a field made mandatory by a rule reads there as
                // not-required — wrong about exactly the fields a caller most needs, and
                // wrong in the silent direction.
                mergedFields,
                SortStates(detail?.States),
                SortTransitions(detail?.Transitions),
                // 🔴 …but the emptiness is LABELLED. Without this, a type whose fetch failed
                // is byte-identical to one that genuinely has nothing, and a reader diffing
                // two documents sees a clean diff where one side simply failed to ask. When
                // the whole detail call failed, every part is unfetched.
                //
                // 🔴 `picklists` is labelled at the TYPE level even though the fetch is
                // org-wide (AB#237): an unresolved value constraint is indistinguishable from
                // a process whose fields are genuinely unconstrained — this ticket's own lie,
                // arriving through a failed fetch instead of a bad guess.
                SortUnfetched(detail is null
                    ? [.. WholeTypeUnfetched, .. PicklistUnfetched(constraints, mergedFields)]
                    : [.. detail.Unfetched ?? [], .. PicklistUnfetched(constraints, mergedFields)])));
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
    /// <para>
    /// 🔴 <c>rules</c> is in this list (AB#236). Requiredness is merged from the rules route,
    /// so a failed rules call means the document's requiredness answer is incomplete — and
    /// unlabelled that reads as a positive claim that nothing is conditionally required.
    /// </para>
    /// <para>
    /// 🔴 <c>picklists</c> is deliberately NOT in this list. It is appended separately (see
    /// <see cref="PicklistUnfetched"/>) because the picklist fetch is ORG-scoped and
    /// independent of the per-type detail call: a type whose detail failed may still have a
    /// perfectly good constraint answer, and claiming otherwise would be a false reservation.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyList<string> WholeTypeUnfetched =
        ["fields", "rules", "states", "transitions"];

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
    /// <para>
    /// 🔴 <b>This is where the two requiredness sources are merged (AB#236).</b> The per-type
    /// fields route reports UNCONDITIONAL requiredness only. A field made mandatory by a rule
    /// — <i>when State = Done → makeRequired</i> — reads as not-required there. Verified live:
    /// <c>Custom.WayfinderAnswer</c> is <c>required: null</c> on the fields route while the
    /// rules route carries a <c>makeRequired</c> action for it. A document built from the
    /// fields source alone is wrong about exactly the fields a caller most needs, and wrong
    /// in the silent direction.
    /// </para>
    /// <para>
    /// 🔴 <b><paramref name="rules"/> being <c>null</c> is NOT the same as it being empty.</b>
    /// <c>null</c> means the rules call failed — <c>rules</c> is then in the type's unfetched
    /// list — and an unconditionally-required field is still reported as required, while a
    /// field the fields route says nothing about is reported as <see
    /// cref="FieldRequirednessKind.Never"/> because that is all the surviving source knows.
    /// The unfetched label is what stops that reading as a positive claim.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ProcessDescriptionField> MergeFields(
        IReadOnlyList<ProcessTypeField>? fields,
        IReadOnlyList<ProcessRule>? rules,
        IReadOnlyDictionary<string, FieldValueConstraint>? constraints)
    {
        if (fields is null)
            return [];

        var conditionsByField = BuildRequirednessIndex(rules);

        return
        [
            .. fields
                .Select(f => new ProcessDescriptionField(
                    f.ReferenceName,
                    f.Name,
                    f.Type,
                    f.DefaultValue,
                    ResolveRequiredness(f, conditionsByField),
                    ResolveValueConstraint(f, constraints),
                    f.Customization,
                    f.IsLocked,
                    f.Description))
                .OrderBy(static f => f.ReferenceName, StringComparer.Ordinal)
                .ThenBy(static f => f.Name, StringComparer.Ordinal)
                .ThenBy(static f => f.Type, StringComparer.Ordinal)
                // 🔴 The chain is total over every DOCUMENT-VISIBLE member, not merely over the
                // three that identify a field. Two rows agreeing on reference name, display
                // name and type but differing in requiredness would otherwise fall through
                // OrderBy's stability to WIRE order — reintroducing exactly the
                // non-determinism this class exists to remove, and silently. Adding a member to
                // ProcessDescriptionField without extending this chain is how that regresses.
                .ThenBy(static f => f.Requiredness.Kind)
                .ThenBy(static f => CanonicalRequirednessKey(f.Requiredness), StringComparer.Ordinal)
                // 🔴 …which is why the value constraint is here too (AB#237). Both its KIND
                // and its resolved values participate: two rows alike in every other member
                // but constrained to different lists must order deterministically, and
                // ordering on the kind alone would leave that tie falling through to wire
                // order.
                .ThenBy(static f => f.ValueConstraint.Kind)
                .ThenBy(static f => CanonicalValueConstraintKey(f.ValueConstraint), StringComparer.Ordinal)
                .ThenBy(static f => f.Customization, StringComparer.Ordinal)
                .ThenBy(static f => f.DefaultValue, StringComparer.Ordinal)
                .ThenBy(static f => f.IsLocked)
                .ThenBy(static f => f.Description, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The value constraint a field carries once the org-scoped picklist source has been
    /// consulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Three outcomes and no fourth, and none of them is a guess.</b> A field the org
    /// route reports as not list-backed is <see cref="FieldValueConstraintKind.Unconstrained"/>
    /// as a stated server FACT. There is deliberately no branch here that reads the field's
    /// name or type — the API's explicit negative makes name-matching unnecessary as well as
    /// banned, and a heuristic would be wrong in both directions on this org's own data.
    /// </para>
    /// <para>
    /// 🔴 <b><paramref name="constraints"/> being <c>null</c> is NOT the same as it being
    /// empty.</b> <c>null</c> means the picklist call failed — <c>picklists</c> is then in the
    /// type's unfetched list — and every field is <see cref="FieldValueConstraint.Unknown"/>
    /// rather than unconstrained. Collapsing the two would let "we could not read the lists"
    /// render as "the server accepts anything here", which is this ticket's own lie.
    /// </para>
    /// <para>
    /// 🔴 A field MISSING from a non-null map is <see cref="FieldValueConstraint.Unknown"/>
    /// too, not unconstrained. The map is org-scoped and the field list is type-scoped, so a
    /// row present on one and absent from the other is a source disagreement — and inventing
    /// an answer for it would be a confident claim nothing supports.
    /// </para>
    /// <para>
    /// 🔴 The lookup is case-INSENSITIVE because the index is. These are two different routes,
    /// and an ordinal-exact join would silently drop a real constraint over a casing
    /// difference, reporting a list-backed field as unconstrained — byte-identical to a field
    /// that genuinely is, and with no unfetched label to catch it. Every other reference-name
    /// comparison in this layer is <c>OrdinalIgnoreCase</c> for the same reason.
    /// </para>
    /// </remarks>
    private static FieldValueConstraint ResolveValueConstraint(
        ProcessTypeField field,
        IReadOnlyDictionary<string, FieldValueConstraint>? constraints)
    {
        if (constraints is null)
            return FieldValueConstraint.Unknown;

        if (!constraints.TryGetValue(field.ReferenceName, out var constraint))
            return FieldValueConstraint.Unknown;

        // Only the list-bearing cases carry values to sort; the rest are already canonical.
        if (constraint.Kind is not (FieldValueConstraintKind.ListConstrained
            or FieldValueConstraintKind.ListSuggested))
        {
            return constraint;
        }

        // 🔴 The values are sorted HERE and nowhere else. Picklist items arrive in the order
        // whoever authored the list happened to type them — an order Twig cannot defend and
        // the server does not promise stable. Sorting in the fetch layer instead would put a
        // second ordering authority in the system; sorting in neither would break
        // byte-stability in a way a single-run unit test cannot see.
        return constraint with
        {
            Values =
            [
                .. constraint.Values
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static v => v, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>A total, stable string form of a field's whole value-constraint answer.</summary>
    /// <remarks>
    /// Used only as an ordering tiebreak. Includes the list name AND the values, so two
    /// otherwise-identical rows constrained to different lists cannot tie and fall through to
    /// wire order.
    /// <para>
    /// 🔴 LENGTH-PREFIXED rather than separator-joined, unlike its requiredness sibling.
    /// Picklist values are arbitrary user-authored strings, so — unlike an ADO condition verb
    /// or field reference name — no character can be assumed absent from them. A separator
    /// convention would be a guarantee this system does not actually have, and two different
    /// value sets colliding onto one key would order ambiguously. A null list name is encoded
    /// distinctly from an empty one for the same totality reason.
    /// </para>
    /// </remarks>
    private static string CanonicalValueConstraintKey(FieldValueConstraint constraint)
    {
        var key = new System.Text.StringBuilder();

        key.Append(constraint.ListName is null ? "~" : $"{constraint.ListName.Length}:{constraint.ListName}");

        foreach (var value in constraint.Values)
            key.Append('|').Append(value.Length).Append(':').Append(value);

        return key.ToString();
    }

    /// <summary>
    /// The <c>picklists</c> unfetched label, when any field's value constraint went unresolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 A per-TYPE label for an ORG-wide source, deliberately. The document's unfetched list
    /// is where a reader looks to find out what a type's silence means, and an unresolved
    /// constraint makes that type's fields silent about accepted values. A header-only notice
    /// would be read past by exactly the reader diffing two types.
    /// </para>
    /// <para>
    /// 🔴 <b>Derived from the RESOLVED fields, not from the map's nullness, and that
    /// distinction is the whole point.</b> A total failure is not the only way to get an
    /// unresolved answer: one picklist's fetch can fail while the field list succeeds, a field
    /// can be absent from the org-scoped map entirely, and the org route can report a field as
    /// list-backed while omitting the pointer. Labelling only the total failure would let the
    /// document say <c>valueConstraint: unknown</c> on a field while its type's unfetched list
    /// is EMPTY — simultaneously claiming "I could not read this" and "everything was read".
    /// That is the ticket's own failure mode arriving one layer down.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> PicklistUnfetched(
        IReadOnlyDictionary<string, FieldValueConstraint>? constraints,
        IReadOnlyList<ProcessDescriptionField> fields)
        => constraints is null
            || fields.Any(static f => f.ValueConstraint.Kind == FieldValueConstraintKind.Unknown)
                ? ["picklists"]
                : [];

    /// <summary>A total, stable string form of a field's whole requiredness answer.</summary>
    /// <remarks>
    /// Used only as an ordering tiebreak. Built from <see cref="CanonicalClauseKey"/> so the
    /// ordering primitive and the de-duplication primitive cannot drift apart.
    /// </remarks>
    private static string CanonicalRequirednessKey(FieldRequiredness requiredness)
        => string.Join("\u001d", requiredness.Conditions.Select(
            static c => CanonicalClauseKey(c.Clauses)));

    /// <summary>
    /// The requiredness a field has once BOTH sources have been consulted.
    /// </summary>
    /// <remarks>
    /// 🔴 Unconditional wins. A field the fields route already calls required stays
    /// <see cref="FieldRequirednessKind.Always"/> even when a rule also makes it required
    /// under a condition: reporting it as conditional would tell a caller it may omit the
    /// field outside that condition, which is false and is a lie in the DANGEROUS direction —
    /// the create call fails at the server.
    /// </remarks>
    private static FieldRequiredness ResolveRequiredness(
        ProcessTypeField field,
        IReadOnlyDictionary<string, List<FieldRequirednessCondition>> conditionsByField)
    {
        if (field.RequiredUnconditionally)
            return FieldRequiredness.Always;

        // 🔴 The lookup is case-INSENSITIVE because the index is (see BuildRequirednessIndex).
        // An ordinal-exact join would drop a rule whose targetField differs only in casing from
        // the fields route's spelling, and drop it into FieldRequirednessKind.Never — which is
        // byte-identical to a field nobody makes required. That is a confident wrong answer in
        // the silent direction: the exact defect this ticket removes, reintroduced as a failed
        // JOIN rather than a failed fetch, and with no Unfetched label to catch it.
        if (!conditionsByField.TryGetValue(field.ReferenceName, out var conditions))
            return FieldRequiredness.Never;

        // An UNCONDITIONED makeRequired rule contributes a condition with no clauses, and
        // that is unconditional requiredness however the fields route reported it. Reporting
        // it as "conditional" would print a warning naming no state, no field and no value —
        // a reservation the reader cannot act on.
        if (conditions.Any(static c => c.Clauses.Count == 0))
            return FieldRequiredness.Always;

        return FieldRequiredness.Conditionally(conditions);
    }

    /// <summary>
    /// Indexes every <c>makeRequired</c> action in the rule set by its target field, with the
    /// conditions under which it fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Every collection built here is ORDINAL-sorted and de-duplicated before it reaches
    /// the document.</b> Rules arrive in SERVER order, and the ~54 inherited rules on a
    /// derived type are exactly the volume that would make an unsorted carry break
    /// byte-stability — the single most important property of this feature. The sort is
    /// decided here and nowhere else.
    /// </para>
    /// <para>
    /// Disabled rules are skipped: a disabled rule does not fire, so reporting a field as
    /// required because of one would be a false positive.
    /// </para>
    /// <para>
    /// Action and condition verbs arrive with an inconsistent leading <c>$</c> across
    /// routes (<c>$makeRequired</c> vs <c>makeRequired</c>), so the ACTION verb is matched
    /// with the prefix trimmed. The condition verb is carried VERBATIM apart from that same
    /// trim, so a reader is comparing the server's own vocabulary rather than Twig's
    /// paraphrase — but trimmed, because otherwise the same rule could diff dirty between
    /// two documents merely because one route spelled it with the sigil.
    /// </para>
    /// <para>
    /// 🔴 <b>The join key is matched case-INSENSITIVELY.</b> The key is the rules route's
    /// <c>targetField</c> matched against the fields route's <c>referenceName</c> — two
    /// different routes, and this route family is already known to be inconsistent about
    /// spelling (hence the sigil trim below). An ordinal-exact join would silently drop a rule
    /// over a casing difference and report the field as not-required, which is byte-identical
    /// to a field nobody makes required and carries no <c>Unfetched</c> label. That is the
    /// exact silent lie this ticket removes. Every other reference-name comparison in this
    /// layer is <c>OrdinalIgnoreCase</c> for the same reason.
    /// </para>
    /// </remarks>
    private static Dictionary<string, List<FieldRequirednessCondition>> BuildRequirednessIndex(
        IReadOnlyList<ProcessRule>? rules)
    {
        var index = new Dictionary<string, List<FieldRequirednessCondition>>(
            StringComparer.OrdinalIgnoreCase);
        if (rules is null || rules.Count == 0)
            return index;

        // Keyed on the field, then on a canonical string form of the condition set, so two
        // rules imposing the SAME condition on the same field collapse to one entry rather
        // than printing the requirement twice. Case-insensitive on the field for the same
        // reason the index is: two spellings of one field are one field.
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule.IsDisabled)
                continue;

            var clauses = SortClauses(rule.Conditions);

            foreach (var action in rule.Actions)
            {
                if (!IsMakeRequired(action.ActionType) ||
                    string.IsNullOrWhiteSpace(action.TargetField))
                {
                    continue;
                }

                var key = CanonicalClauseKey(clauses);
                if (!seen.TryGetValue(action.TargetField, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    seen[action.TargetField] = keys;
                    index[action.TargetField] = [];
                }

                if (keys.Add(key))
                    index[action.TargetField].Add(new FieldRequirednessCondition(clauses));
            }
        }

        // 🔴 The conditions on ONE field are themselves sorted, on the same canonical key the
        // de-duplication used. Without this the alternatives would appear in the order the
        // server happened to list the rules, and two documents taken against an unchanged
        // process could differ.
        foreach (var conditions in index.Values)
            conditions.Sort(static (l, r) => string.CompareOrdinal(
                CanonicalClauseKey(l.Clauses), CanonicalClauseKey(r.Clauses)));

        return index;
    }

    /// <remarks>
    /// Clauses within one condition are conjunctive, so their order carries no meaning and
    /// sorting them is lossless. It is also required: the server's order is not promised
    /// stable, and an unsorted carry would let two documents differ over the same rule.
    /// </remarks>
    private static IReadOnlyList<FieldRequirednessClause> SortClauses(
        IReadOnlyList<RuleCondition> conditions)
        =>
        [
            .. conditions
                .Select(static c => new FieldRequirednessClause(
                    TrimRuleSigil(c.ConditionType), c.Field, c.Value))
                .OrderBy(static c => c.ConditionType, StringComparer.Ordinal)
                .ThenBy(static c => c.Field, StringComparer.Ordinal)
                .ThenBy(static c => c.Value, StringComparer.Ordinal),
        ];

    /// <summary>A total, stable string form of a clause set, used to sort and de-duplicate.</summary>
    /// <remarks>
    /// Uses a separator pair that cannot occur in an ADO condition verb, field reference name
    /// or value, so two different clause sets cannot collide onto one key and be silently
    /// merged into a single reported condition.
    /// </remarks>
    private static string CanonicalClauseKey(IReadOnlyList<FieldRequirednessClause> clauses)
        => string.Join("\u001f", clauses.Select(static c =>
            $"{c.ConditionType}\u001e{c.Field}\u001e{c.Value}"));

    /// <remarks>
    /// Matched with the leading <c>$</c> trimmed and case-insensitively, because the rules
    /// route is not consistent about either across api-versions and customization types.
    /// Missing a <c>makeRequired</c> over a sigil would silently reinstate the exact defect
    /// AB#236 fixes.
    /// </remarks>
    private static bool IsMakeRequired(string actionType)
        => string.Equals(TrimRuleSigil(actionType), "makeRequired", StringComparison.OrdinalIgnoreCase);

    private static string TrimRuleSigil(string value)
        => value.TrimStart('$');

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
