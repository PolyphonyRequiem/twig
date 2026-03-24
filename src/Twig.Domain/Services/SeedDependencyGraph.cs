using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Produces a topological publish order from seed links and ParentId edges.
/// Detects cycles and reports cyclic node IDs.
/// </summary>
public static class SeedDependencyGraph
{
    /// <summary>
    /// Returns seeds in topological publish order with cycle detection.
    /// Seeds with no incoming edges publish first. Unconnected seeds are
    /// ordered by <see cref="WorkItem.SeedCreatedAt"/> for determinism.
    /// </summary>
    public static (IReadOnlyList<int> Order, IReadOnlySet<int> CyclicIds) Sort(
        IReadOnlyList<WorkItem> seeds,
        IReadOnlyList<SeedLink> links)
    {
        var seedIds = new HashSet<int>(seeds.Select(s => s.Id));
        var adjacency = new Dictionary<int, HashSet<int>>();

        // Register all seed nodes
        foreach (var seed in seeds)
            adjacency[seed.Id] = new HashSet<int>();

        // Add ParentId < 0 edges: parent → child (publish parent first)
        foreach (var seed in seeds)
        {
            if (seed.ParentId.HasValue && seed.ParentId.Value < 0 && seedIds.Contains(seed.ParentId.Value))
                adjacency[seed.ParentId.Value].Add(seed.Id);
        }

        // Add seed_links edges based on directionality
        foreach (var link in links)
        {
            if (!seedIds.Contains(link.SourceId) || !seedIds.Contains(link.TargetId))
                continue;

            switch (link.LinkType)
            {
                case SeedLinkTypes.DependsOn:
                case SeedLinkTypes.BlockedBy:
                    adjacency[link.TargetId].Add(link.SourceId);
                    break;

                case SeedLinkTypes.Blocks:
                case SeedLinkTypes.DependedOnBy:
                    adjacency[link.SourceId].Add(link.TargetId);
                    break;
            }
        }

        // Kahn's algorithm with SeedCreatedAt tiebreaking
        var seedLookup = seeds.ToDictionary(s => s.Id);
        var inDegree = new Dictionary<int, int>();
        foreach (var node in adjacency.Keys)
            inDegree[node] = 0;

        foreach (var (from, toSet) in adjacency)
        {
            foreach (var to in toSet)
            {
                if (inDegree.ContainsKey(to))
                    inDegree[to]++;
            }
        }

        var ready = new SortedSet<int>(Comparer<int>.Create((a, b) =>
        {
            var aTime = seedLookup.TryGetValue(a, out var sa) ? sa.SeedCreatedAt : null;
            var bTime = seedLookup.TryGetValue(b, out var sb) ? sb.SeedCreatedAt : null;
            var cmp = (aTime ?? DateTimeOffset.MaxValue).CompareTo(bTime ?? DateTimeOffset.MaxValue);
            return cmp != 0 ? cmp : a.CompareTo(b);
        }));

        foreach (var (node, degree) in inDegree)
        {
            if (degree == 0)
                ready.Add(node);
        }

        var result = new List<int>();
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            result.Add(current);

            foreach (var dep in adjacency[current])
            {
                if (!inDegree.ContainsKey(dep))
                    continue;

                inDegree[dep]--;
                if (inDegree[dep] == 0)
                    ready.Add(dep);
            }
        }

        // Any remaining nodes with non-zero in-degree are part of cycles
        var cyclicIds = new HashSet<int>();
        foreach (var (node, degree) in inDegree)
        {
            if (degree > 0)
                cyclicIds.Add(node);
        }

        return (result, cyclicIds);
    }
}
