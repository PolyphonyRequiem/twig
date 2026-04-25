namespace Twig.Domain.Services;

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
}
