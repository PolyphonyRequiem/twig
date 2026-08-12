namespace Twig.Domain.ValueObjects;

/// <summary>
/// One backlog level a work item type belongs to — the membership edge between a type and a
/// behaviour, resolved to something a reader can recognise.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Two routes, and the membership route alone is not readable.</b> Per-type membership
/// lives at <c>_apis/work/processes/{id}/workItemTypesBehaviors/{ref}/behaviors</c> — note
/// <c>workItemTypesBehaviors</c>, not <c>workItemTypes/{ref}/behaviors</c>, which returns an
/// HTML 404 for every type on every arm, verified live 2026-08-12. That route returns a bare
/// reference: <c>{"behavior":{"id":"Custom.3daa…"},"isDefault":true}</c>. A custom backlog
/// level's id is a GUID, so a document carrying the edge alone would say a type belongs to
/// <c>Custom.3daa3b35-2574-4c94-b260-0d15fe6db82f</c> — true, unreadable, and useless in a
/// diff between two processes that named the same level differently.
/// </para>
/// <para>
/// So the process-level behaviour CATALOGUE is fetched once per run and joined onto the edge
/// to supply <see cref="Name"/> and <see cref="Rank"/>. The join is
/// <c>OrdinalIgnoreCase</c> on the reference name, like every other cross-route name match in
/// this layer.
/// </para>
/// <para>
/// 🔴 <b>An unresolved name is EMPTY and the reference name is still carried, rather than the
/// membership being dropped.</b> "This type is on a backlog level we could not name" is a
/// weaker claim than the full one but a much stronger one than silence — dropping the edge
/// would let a real membership difference diff clean, which is the omission this whole
/// feature exists to prevent. The type's <c>behaviourCatalogue</c> unfetched label is what
/// tells the reader why the name is missing.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch
/// docs/process-descriptor-map)</c> Implementation Decision 4 ("Behaviour membership — which
/// backlog levels the type belongs to"). Evidence:
/// <c>wayfinder-process-descriptor/assets/0001-endpoint-findings.md</c> § Behaviors.
/// </para>
/// </remarks>
/// <param name="ReferenceName">
/// The behaviour's stable identity, e.g. <c>Microsoft.VSTS.Basic.EpicBacklogBehavior</c> or
/// <c>Custom.3daa3b35-…</c>. 🔴 This is what two processes are matched on; the display name
/// is for the reader.
/// </param>
/// <param name="Name">
/// The behaviour's display name from the process-level catalogue, or empty when the catalogue
/// could not be read or does not report this behaviour.
/// </param>
/// <param name="Rank">
/// The catalogue's ordering hint — where the level sits in the backlog hierarchy — or
/// <c>null</c> when unresolved. Carried as a fact, not used for ordering: the document orders
/// memberships by reference name so two processes that ranked the same level differently still
/// line up line-for-line.
/// </param>
/// <param name="IsDefault">
/// Whether this is the type's DEFAULT behaviour — the level a new item of this type lands on.
/// A real difference between two processes, so it is carried rather than flattened away.
/// </param>
internal sealed record ProcessBehaviourMembership(
    string ReferenceName,
    string Name,
    int? Rank,
    bool IsDefault);

/// <summary>
/// One backlog level as the process-level behaviour catalogue reports it.
/// </summary>
/// <remarks>
/// The catalogue is process-scoped and costs ONE call per run regardless of type count, so it
/// is fetched once and joined rather than re-asked per type. It is not itself part of the
/// per-type document — only the names and ranks it supplies are.
/// </remarks>
/// <param name="ReferenceName">The behaviour's stable identity. The join key.</param>
/// <param name="Name">The display name, e.g. <c>Wayfinding</c>, <c>Epics</c>.</param>
/// <param name="Rank">Where the level sits in the backlog hierarchy.</param>
internal sealed record ProcessBehaviourSummary(
    string ReferenceName,
    string Name,
    int? Rank);
