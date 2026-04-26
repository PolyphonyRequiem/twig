using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Read model for the area-filtered workspace view. Contains work items matching
/// configured area paths plus hydrated parent context, organized into a hierarchy.
/// </summary>
public sealed class AreaView
{
    /// <summary>Work items that match at least one configured area path filter.</summary>
    public IReadOnlyList<WorkItem> AreaItems { get; }

    /// <summary>Configured area path filters used for this view.</summary>
    public IReadOnlyList<AreaPathFilter> Filters { get; }

    /// <summary>Hierarchy tree with IsSprintItem indicating area membership.</summary>
    public SprintHierarchy? Hierarchy { get; }

    /// <summary>Total count of items matching the area filters (before parent hydration).</summary>
    public int MatchCount { get; }

    private AreaView(
        IReadOnlyList<WorkItem> areaItems,
        IReadOnlyList<AreaPathFilter> filters,
        SprintHierarchy? hierarchy,
        int matchCount)
    {
        AreaItems = areaItems;
        Filters = filters;
        Hierarchy = hierarchy;
        MatchCount = matchCount;
    }

    /// <summary>
    /// Builds an immutable <see cref="AreaView"/> from area items, filters, and hierarchy.
    /// </summary>
    public static AreaView Build(
        IReadOnlyList<WorkItem> areaItems,
        IReadOnlyList<AreaPathFilter> filters,
        SprintHierarchy? hierarchy = null,
        int matchCount = 0)
    {
        return new AreaView(areaItems, filters, hierarchy, matchCount);
    }
}
