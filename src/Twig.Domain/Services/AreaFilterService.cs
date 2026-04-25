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

        return items.Where(i => IsInArea(i.AreaPath, filters)).ToList();
    }

    /// <summary>
    /// Checks whether a single area path matches ANY of the configured filters.
    /// </summary>
    public static bool IsInArea(
        AreaPath itemPath,
        IReadOnlyList<AreaPathFilter> filters)
        => filters.Any(f => f.Matches(itemPath));
}
