using Twig.Domain.ReadModels;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Determines whether a work item type is above the configured working level
/// in the backlog hierarchy. Used to dim parent chain items that are above the
/// user's day-to-day working level (e.g., Epics above a "Task" working level).
/// </summary>
public static class WorkingLevelResolver
{
    /// <summary>
    /// Returns true if <paramref name="itemTypeName"/> is above (higher in the hierarchy than)
    /// <paramref name="workingLevelTypeName"/>. Lower level numbers are higher in the hierarchy.
    /// Returns false when either type is unknown — safe fallback (no dimming).
    /// </summary>
    public static bool IsAboveWorkingLevel(
        string itemTypeName,
        string workingLevelTypeName,
        IReadOnlyDictionary<string, int> typeLevelMap)
    {
        if (!typeLevelMap.TryGetValue(workingLevelTypeName, out var workingLevel))
            return false;
        if (!typeLevelMap.TryGetValue(itemTypeName, out var itemLevel))
            return false;
        return itemLevel < workingLevel;
    }

    /// <summary>
    /// Prunes ancestor nodes that exceed <paramref name="depthUp"/> levels above the working level.
    /// Nodes beyond the limit are removed and their children promoted as new roots.
    /// Returns the original list unchanged when working level or type map is unavailable.
    /// </summary>
    public static IReadOnlyList<SprintHierarchyNode> PruneAncestors(
        IReadOnlyList<SprintHierarchyNode> roots,
        string? workingLevelTypeName,
        IReadOnlyDictionary<string, int>? typeLevelMap,
        int depthUp)
    {
        if (workingLevelTypeName is null || typeLevelMap is null)
            return roots;

        if (!typeLevelMap.TryGetValue(workingLevelTypeName, out var workingLevel))
            return roots;

        var result = new List<SprintHierarchyNode>();
        foreach (var root in roots)
            CollectPrunedRoots(root, workingLevel, typeLevelMap, depthUp, result);

        return result.Count > 0 ? result : roots;
    }

    private static void CollectPrunedRoots(
        SprintHierarchyNode node, int workingLevel,
        IReadOnlyDictionary<string, int> typeLevelMap,
        int depthUp, List<SprintHierarchyNode> result)
    {
        if (node.IsVirtualGroup)
        {
            result.Add(node);
            return;
        }

        if (!typeLevelMap.TryGetValue(node.Item.Type.Value, out var nodeLevel))
        {
            result.Add(node);
            return;
        }

        if (workingLevel - nodeLevel > depthUp)
        {
            foreach (var child in node.Children)
                CollectPrunedRoots(child, workingLevel, typeLevelMap, depthUp, result);
        }
        else
        {
            result.Add(node);
        }
    }
}
