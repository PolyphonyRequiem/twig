using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// What Benches exist and which one is current (ADO #148, docs/specs/bench.spec.md §5).
/// </summary>
/// <remarks>
/// 🔴 Which Bench is CURRENT is carried here rather than being left for the adapter to infer from
/// <see cref="Bench.IsDefault"/>. Today the current Bench IS the default one, but switching (#149)
/// makes those two different questions, and an adapter that had inferred one from the other would
/// keep rendering the wrong marker with no test able to see it — the listing would simply say the
/// wrong thing.
/// </remarks>
/// <param name="Benches">Every Bench that exists, ordered by name.</param>
/// <param name="CurrentBenchName">
/// The name of the Bench commands act on. Always one of <paramref name="Benches"/>, because the
/// default Bench cannot go missing (spec §4).
/// </param>
public sealed record BenchListing(
    IReadOnlyList<Bench> Benches,
    string CurrentBenchName);
