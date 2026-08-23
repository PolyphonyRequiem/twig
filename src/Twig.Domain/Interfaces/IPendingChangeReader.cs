using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Read-only snapshot access to the staged pending-change journal. Segregated from
/// <see cref="IPendingChangeStore"/> so consumers that only need to observe the journal —
/// plan preview, save/discard diagnostics, MCP status — do not depend on the mutating store.
/// </summary>
/// <remarks>
/// Implementations MAY be the same concrete type that implements
/// <see cref="IPendingChangeStore"/>; this interface is a segregation contract, not an
/// alternative persistence path.
/// </remarks>
public interface IPendingChangeReader
{
    /// <summary>
    /// Returns every staged pending change as a single ordered snapshot, joined against
    /// <c>staged_identities</c> and <c>publish_id_map</c> so the caller can see the seed
    /// identity behind each row without a second round-trip.
    /// <para>
    /// Ordered by <c>pending_changes.id</c> globally — not per work item — so repeated edits
    /// stay in the exact sequence they were staged in. Raw values, HTML, and unknown kinds
    /// are preserved verbatim; the read never collapses, rewrites, or mutates the journal.
    /// </para>
    /// <para>
    /// If a work item ID matches more than one <c>staged_identities.alias</c> or more than
    /// one <c>publish_id_map.new_id</c> the read throws <see cref="InvalidOperationException"/>
    /// rather than picking one silently. A pre-0014 seed whose alias no longer resolves
    /// through the durable register is returned with <see cref="PendingChangeDetail.SeedRemap"/>
    /// set to <see langword="null"/>.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PendingChangeDetail>> GetAllChangesAsync(CancellationToken ct = default);
}
