using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Value object representing the set of work items relevant to the current context.
/// <c>AllIds</c> is the union of all ID collections, computed on every access.
/// </summary>
public sealed record WorkingSet
{
    public int? ActiveItemId { get; init; }
    public IReadOnlyList<int> ParentChainIds { get; init; } = [];
    public IReadOnlyList<int> ChildrenIds { get; init; } = [];
    public IReadOnlyList<int> SprintItemIds { get; init; } = [];
    public IReadOnlyList<int> SeedIds { get; init; } = [];
    public IReadOnlySet<int> DirtyItemIds { get; init; } = new HashSet<int>();
    public IterationPath IterationPath { get; init; }

    /// <summary>
    /// Union of all ID sets. Computed fresh on each access to avoid stale results after <c>with</c> expressions.
    /// </summary>
    public IReadOnlySet<int> AllIds => ComputeAllIds();

    private IReadOnlySet<int> ComputeAllIds()
    {
        var set = new HashSet<int>();

        if (ActiveItemId.HasValue)
            set.Add(ActiveItemId.Value);

        foreach (var id in ParentChainIds) set.Add(id);
        foreach (var id in ChildrenIds) set.Add(id);
        foreach (var id in SprintItemIds) set.Add(id);
        foreach (var id in SeedIds) set.Add(id);
        foreach (var id in DirtyItemIds) set.Add(id);

        return set;
    }
}
