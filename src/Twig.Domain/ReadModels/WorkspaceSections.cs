using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// A mode-labelled section of workspace items (e.g., Sprint, Area, Recent, Manual).
/// When <see cref="TreeRoots"/> is provided, the section renders as a hierarchical tree;
/// otherwise it falls back to flat item listing.
/// </summary>
public sealed record WorkspaceSection(
    string ModeName,
    IReadOnlyList<WorkItem> Items,
    IReadOnlyList<SprintHierarchyNode>? TreeRoots = null);

/// <summary>
/// Read model that partitions workspace items into mode-labelled sections with
/// first-mode-wins deduplication. Manual inclusions always appear in the Manual
/// section regardless of prior appearance. Empty sections are omitted.
/// </summary>
public sealed class WorkspaceSections
{
    /// <summary>Non-empty mode sections in display order.</summary>
    public IReadOnlyList<WorkspaceSection> Sections { get; }

    /// <summary>IDs of items manually excluded from the workspace.</summary>
    public IReadOnlyList<int> ExcludedItemIds { get; }

    private WorkspaceSections(IReadOnlyList<WorkspaceSection> sections, IReadOnlyList<int> excludedItemIds)
    {
        Sections = sections;
        ExcludedItemIds = excludedItemIds;
    }

    /// <summary>
    /// Builds mode sections with first-mode-wins deduplication.
    /// Items are assigned to the first mode in which they appear (Sprint → Area → Recent).
    /// Manual inclusions always appear in the Manual section, even if already shown elsewhere.
    /// Empty sections and empty excluded-ID lists are handled gracefully.
    /// </summary>
    public static WorkspaceSections Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyList<WorkItem>? areaItems = null,
        IReadOnlyList<WorkItem>? recentItems = null,
        IReadOnlyList<WorkItem>? manualItems = null,
        IReadOnlyList<int>? excludedIds = null,
        IReadOnlyList<SprintHierarchyNode>? treeRoots = null)
    {
        var seen = new HashSet<int>();
        var sections = new List<WorkspaceSection>();

        AddSection(sections, "Sprint", sprintItems, seen, dedup: true, treeRoots);
        AddSection(sections, "Area", areaItems, seen, dedup: true);
        AddSection(sections, "Recent", recentItems, seen, dedup: true);

        // Manual inclusions always show — no dedup against prior sections
        AddSection(sections, "Manual", manualItems, seen, dedup: false);

        return new WorkspaceSections(sections, excludedIds ?? Array.Empty<int>());
    }

    /// <summary>
    /// Builds workspace sections from pre-constructed section records. Used when
    /// multiple sections each carry their own tree roots (e.g., in tests or future
    /// multi-mode tree rendering).
    /// </summary>
    public static WorkspaceSections BuildWithTreeRoots(
        IReadOnlyList<WorkspaceSection> sections,
        IReadOnlyList<int>? excludedIds = null)
    {
        return new WorkspaceSections(sections, excludedIds ?? Array.Empty<int>());
    }

    private static void AddSection(
        List<WorkspaceSection> sections,
        string modeName,
        IReadOnlyList<WorkItem>? items,
        HashSet<int> seen,
        bool dedup,
        IReadOnlyList<SprintHierarchyNode>? treeRoots = null)
    {
        if (items is null || items.Count == 0)
            return;

        var sectionItems = new List<WorkItem>();
        foreach (var item in items)
        {
            if (!dedup || seen.Add(item.Id))
                sectionItems.Add(item);
        }

        if (sectionItems.Count > 0)
            sections.Add(new WorkspaceSection(modeName, sectionItems, treeRoots));
    }
}
