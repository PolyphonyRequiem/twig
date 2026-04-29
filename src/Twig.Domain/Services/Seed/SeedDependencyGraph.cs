using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Seed;

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
        var (_, adjacency) = BuildAdjacency(seeds, links);

        // Kahn's algorithm with SeedCreatedAt tiebreaking
        var seedLookup = seeds.ToDictionary(s => s.Id);
        var inDegree = ComputeInDegree(adjacency);

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

    /// <summary>
    /// Checks whether adding the proposed link would create a cycle in the dependency graph.
    /// Returns <c>false</c> immediately for non-directional link types (Related, ParentChild)
    /// or when the proposed link endpoints are not in the seed set.
    /// </summary>
    public static bool WouldCreateCycle(
        IReadOnlyList<WorkItem> seeds,
        IReadOnlyList<SeedLink> existingLinks,
        SeedLink proposedLink)
    {
        var (seedIds, adjacency) = BuildAdjacency(seeds, existingLinks);

        if (!AddLinkEdge(adjacency, seedIds, proposedLink))
            return false;

        // Simplified Kahn's — only care about cycle detection, not ordering
        var inDegree = ComputeInDegree(adjacency);

        var ready = new Queue<int>();
        foreach (var (node, degree) in inDegree)
        {
            if (degree == 0)
                ready.Enqueue(node);
        }

        var processedCount = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            processedCount++;

            foreach (var dep in adjacency[current])
            {
                if (!inDegree.ContainsKey(dep))
                    continue;

                inDegree[dep]--;
                if (inDegree[dep] == 0)
                    ready.Enqueue(dep);
            }
        }

        return processedCount < adjacency.Count;
    }

    /// <summary>
    /// Builds the adjacency graph from seeds and their links, including ParentId edges.
    /// </summary>
    private static (HashSet<int> SeedIds, Dictionary<int, HashSet<int>> Adjacency) BuildAdjacency(
        IReadOnlyList<WorkItem> seeds,
        IReadOnlyList<SeedLink> links)
    {
        var seedIds = new HashSet<int>(seeds.Select(s => s.Id));
        var adjacency = new Dictionary<int, HashSet<int>>();

        foreach (var seed in seeds)
            adjacency[seed.Id] = new HashSet<int>();

        // ParentId < 0 edges: parent → child (publish parent first)
        foreach (var seed in seeds)
        {
            if (seed.ParentId.HasValue && seed.ParentId.Value < 0 && seedIds.Contains(seed.ParentId.Value))
                adjacency[seed.ParentId.Value].Add(seed.Id);
        }

        foreach (var link in links)
            AddLinkEdge(adjacency, seedIds, link);

        return (seedIds, adjacency);
    }

    /// <summary>
    /// Adds a directional edge for the given link. Returns <c>true</c> if an edge was added.
    /// </summary>
    private static bool AddLinkEdge(
        Dictionary<int, HashSet<int>> adjacency,
        HashSet<int> seedIds,
        SeedLink link)
    {
        if (!seedIds.Contains(link.SourceId) || !seedIds.Contains(link.TargetId))
            return false;

        switch (link.LinkType)
        {
            case SeedLinkTypes.DependsOn:
            case SeedLinkTypes.BlockedBy:
                adjacency[link.TargetId].Add(link.SourceId);
                return true;

            case SeedLinkTypes.Blocks:
            case SeedLinkTypes.DependedOnBy:
                adjacency[link.SourceId].Add(link.TargetId);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Computes in-degree for each node in the adjacency graph.
    /// </summary>
    private static Dictionary<int, int> ComputeInDegree(Dictionary<int, HashSet<int>> adjacency)
    {
        var inDegree = new Dictionary<int, int>();
        foreach (var node in adjacency.Keys)
            inDegree[node] = 0;

        foreach (var (_, toSet) in adjacency)
        {
            foreach (var to in toSet)
            {
                if (inDegree.ContainsKey(to))
                    inDegree[to]++;
            }
        }

        return inDegree;
    }
}
