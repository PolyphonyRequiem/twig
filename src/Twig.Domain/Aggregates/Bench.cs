using Twig.Domain.ValueObjects;

namespace Twig.Domain.Aggregates;

/// <summary>
/// A named, durable, saved backlog — an arrangement a person names once and returns to
/// (docs/specs/bench.spec.md).
/// <para>
/// A Bench holds SELECTORS and nothing else. Its membership is the UNION of those selectors,
/// and order does not matter. It stores the rule, never the results.
/// </para>
/// <para>
/// 🔴 A Bench is NOT a sync unit. Reconciliation scopes to the pending set, per Connection;
/// switching Bench never changes what twig pushes or pulls. It is also not a record of interest
/// — reading one work item does not add it to a Bench, and must not.
/// </para>
/// <para>
/// Exclusions are OUT of the Bench entirely (decided 2026-08-06). There is no subtracting
/// selector and no way to remove an item a selector matched.
/// </para>
/// </summary>
public sealed record Bench
{
    /// <summary>Storage identity. Zero for a Bench that has not been persisted.</summary>
    public long Id { get; init; }

    /// <summary>The name the person recognises this arrangement by.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether this is the one Bench twig creates on its own (spec §4). The default cannot go
    /// missing, so it is never subject to the unknown-Bench error.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// The rules that decide membership. Held as a set: adding the same selector twice is one
    /// selector, and the order they were added in is not recorded and never observable.
    /// </summary>
    public IReadOnlyCollection<BenchSelector> Selectors { get; init; } = [];

    /// <summary>The name twig gives the Bench it creates on its own.</summary>
    public const string DefaultName = "default";
}
