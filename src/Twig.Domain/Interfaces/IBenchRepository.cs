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

    /// <summary>Returns every Bench, ordered by name.</summary>
    Task<IReadOnlyList<Bench>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds a selector to a Bench. Idempotent: adding the same selector twice leaves one, so
    /// membership cannot be changed by repetition.
    /// </summary>
    Task AddSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default);

    /// <summary>Removes a selector from a Bench. No-op when it is not present.</summary>
    Task RemoveSelectorAsync(long benchId, BenchSelector selector, CancellationToken ct = default);
}
