using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Pure domain logic for area-path matching against work item collections.
/// Stateless, static — no DI registration needed.
/// </summary>
public static class AreaFilterService
{
    /// <summary>
    /// Returns items matching ANY configured filter (OR semantics).
    /// </summary>
    public static IReadOnlyList<WorkItem> FilterByArea(
        IReadOnlyList<WorkItem> items,
        IReadOnlyList<AreaPathFilter> filters)
    {
        if (filters.Count == 0)
            return [];

        var result = new List<WorkItem>();
        foreach (var item in items)
        {
            if (IsInArea(item.AreaPath, filters))
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Checks whether a single area path matches ANY of the configured filters.
    /// </summary>
    public static bool IsInArea(
        AreaPath itemPath,
        IReadOnlyList<AreaPathFilter> filters)
    {
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Matches(itemPath))
                return true;
        }
        return false;
    }
}
