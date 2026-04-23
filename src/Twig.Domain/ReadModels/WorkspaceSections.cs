using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// A mode-labelled section of workspace items (e.g., Sprint, Area, Recent, Manual).
/// </summary>
public sealed record WorkspaceSection(
    string ModeName,
    IReadOnlyList<WorkItem> Items);

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
        IReadOnlyList<int>? excludedIds = null)
    {
        var seen = new HashSet<int>();
        var sections = new List<WorkspaceSection>();

        AddSection(sections, "Sprint", sprintItems, seen, dedup: true);
        AddSection(sections, "Area", areaItems, seen, dedup: true);
        AddSection(sections, "Recent", recentItems, seen, dedup: true);

        // Manual inclusions always show — no dedup against prior sections
        AddSection(sections, "Manual", manualItems, seen, dedup: false);

        return new WorkspaceSections(sections, excludedIds ?? Array.Empty<int>());
    }

    private static void AddSection(
        List<WorkspaceSection> sections,
        string modeName,
        IReadOnlyList<WorkItem>? items,
        HashSet<int> seen,
        bool dedup)
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
            sections.Add(new WorkspaceSection(modeName, sectionItems));
    }
}
