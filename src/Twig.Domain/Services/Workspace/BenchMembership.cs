using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// What a Bench's selectors matched, at one moment, against the local cache.
/// <para>
/// 🔴 Membership is <see cref="AllIds"/> — one flat, order-free union. The per-kind groups exist
/// because the view DISPLAYS a hand pin differently from a query result; they are a presentation
/// split, not two memberships. An item matched by both appears in both groups and once in the
/// union, which is exactly the overlap case the spec requires.
/// </para>
/// </summary>
public sealed record BenchMembership
{
    /// <summary>Items matched by query selectors, in the cache's own order, deduplicated.</summary>
    public IReadOnlyList<WorkItem> QueryMatches { get; init; } = [];

    /// <summary>
    /// Ids matched by item and subtree selectors, deduplicated. Subtree selectors are already
    /// expanded against the cache as it is now.
    /// </summary>
    public IReadOnlyList<int> PinnedIds { get; init; } = [];

    /// <summary>The iterations the sprint rule resolved to, for callers that report scope.</summary>
    public IReadOnlyList<IterationPath> IterationPaths { get; init; } = [];

    /// <summary>The Bench's membership: the union of everything its selectors matched.</summary>
    public IReadOnlySet<int> AllIds
    {
        get
        {
            var set = new HashSet<int>();
            foreach (var item in QueryMatches) set.Add(item.Id);
            foreach (var id in PinnedIds) set.Add(id);
            return set;
        }
    }
}
