using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads and writes Benches in the durable store (docs/specs/bench.spec.md §1).
/// <para>
/// 🔴 A Bench lives in the store that is NEVER dropped. Pins are work the person did by hand and
/// cannot be rebuilt from ADO, and their loss is silent — nothing prompts and nothing refuses,
/// so it surfaces weeks later. Every shape change here is an additive migration.
/// </para>
/// </summary>
public interface IBenchRepository
{
    /// <summary>
    /// Returns the default Bench, creating it on first use if it does not exist.
    /// <para>
    /// The default is the only Bench twig creates on its own (spec §4), so it cannot go missing
    /// and is never subject to the unknown-Bench error. <paramref name="initialSelectors"/> is
    /// applied only at creation — it never overwrites an existing Bench, because that would
    /// discard selectors the person added by hand.
    /// </para>
    /// </summary>
    Task<Bench> GetOrCreateDefaultAsync(
        IReadOnlyCollection<BenchSelector> initialSelectors, CancellationToken ct = default);

    /// <summary>Returns a Bench by name, or null. Name matching is case-insensitive.</summary>
    Task<Bench?> GetByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Creates a non-default Bench with no selectors, or returns null when the name is taken
    /// (ADO #148, spec §5).
    /// <para>
    /// 🔴 Returning null rather than overwriting is the point. A create that quietly adopted an
    /// existing Bench would be the create-on-reference defect twig is escaping: the person would
    /// believe they had a fresh arrangement and would in fact be editing one they already had.
    /// Name matching is case-insensitive, so the caller cannot end up with two Benches a listing
    /// cannot tell apart.
    /// </para>
    /// </summary>
    Task<Bench?> CreateAsync(string name, CancellationToken ct = default);

    /// <summary>Returns every Bench, ordered by name.</summary>
    Task<IReadOnlyList<Bench>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the Bench the person is standing on, or null when they have never switched
    /// (ADO #149, spec §5).
    /// <para>
    /// 🔴 Null means "nobody has switched", NOT "the stored Bench is missing". The caller resolves
    /// null to the default, which cannot go missing. This is deliberately not stored as an eager
    /// pointer to the default at first use: a pointer that must always resolve is one more thing
    /// that can be wrong, and there is nothing for it to say that its absence does not.
    /// </para>
    /// </summary>
    Task<Bench?> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>
    /// Records which Bench is current. Takes a Bench that has already been resolved by name, so a
    /// name that resolves to nothing cannot reach storage.
    /// <para>
    /// 🔴 There is deliberately NO overload taking a name. A store method that accepted a name
    /// would have to decide what to do with one that does not exist, and the only answers are the
    /// two this change exists to refuse: create it, or point at nothing. Resolution happens above,
    /// once, where the unknown-Bench error is raised.
    /// </para>
    /// </summary>
    Task SetCurrentAsync(long benchId, CancellationToken ct = default);

    /// <summary>
    /// Adds a selector to a Bench. Idempotent: adding the same selector twice leaves one, so
    /// membership cannot be changed by repetition.
    /// </summary>
    Task AddSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default);

    /// <summary>Removes a selector from a Bench. No-op when it is not present.</summary>
    Task RemoveSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default);

    /// <summary>
    /// Removes a Bench and the selectors that belong to it (ADO #150, spec §5).
    /// <para>
    /// 🔴 Takes an id, never a name, for the same reason <see cref="SetCurrentAsync"/> does: a
    /// store method that accepted a name would have to decide what a name that resolves to nothing
    /// means, and the only answers are the ones this family of tickets refuses. Whether the Bench
    /// may be deleted at all — and whether the person has been told what it holds — is settled
    /// above, before an id exists.
    /// </para>
    /// <para>
    /// 🔴 The cascade stops at selectors. A Bench is a VIEW: deleting one must not touch the
    /// pending set, which is work twig owes ADO and cannot rebuild. The pointer at the current
    /// Bench is cleared when it named this one, so the caller falls back to the default rather
    /// than standing on a Bench that is gone.
    /// </para>
    /// </summary>
    Task DeleteAsync(long benchId, CancellationToken ct = default);
}
