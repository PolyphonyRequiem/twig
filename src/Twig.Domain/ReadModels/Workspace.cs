using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Projection/composite read model for display — no identity, no invariants.
/// Combines context item, sprint items, and seeds into a unified workspace view.
/// </summary>
public sealed class Workspace
{
    /// <summary>The current context work item (may be null if no context is set).</summary>
    public WorkItem? ContextItem { get; }

    /// <summary>Work items in the current sprint.</summary>
    public IReadOnlyList<WorkItem> SprintItems { get; }

    /// <summary>Seed work items (always included in the workspace).</summary>
    public IReadOnlyList<WorkItem> Seeds { get; }

    /// <summary>Optional sprint hierarchy for hierarchical rendering.</summary>
    public SprintHierarchy? Hierarchy { get; }

    /// <summary>Optional mode-sectioned view of workspace items with dedup.</summary>
    public WorkspaceSections? Sections { get; }

    /// <summary>Manually tracked items (pinned to the workspace via track/track-tree).</summary>
    public IReadOnlyList<TrackedItem> TrackedItems { get; }

    /// <summary>IDs of items manually excluded from the workspace view.</summary>
    public IReadOnlyList<int> ExcludedIds { get; }

    private Workspace(
        WorkItem? context,
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyList<WorkItem> seeds,
        SprintHierarchy? hierarchy,
        WorkspaceSections? sections,
        IReadOnlyList<TrackedItem>? trackedItems,
        IReadOnlyList<int>? excludedIds)
    {
        ContextItem = context;
        SprintItems = sprintItems;
        Seeds = seeds;
        Hierarchy = hierarchy;
        Sections = sections;
        TrackedItems = trackedItems ?? Array.Empty<TrackedItem>();
        ExcludedIds = excludedIds ?? Array.Empty<int>();
    }

    /// <summary>
    /// Builds an immutable <see cref="Workspace"/> from context, sprint, and seed items.
    /// </summary>
    public static Workspace Build(
        WorkItem? context,
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyList<WorkItem> seeds,
        SprintHierarchy? hierarchy = null,
        WorkspaceSections? sections = null,
        IReadOnlyList<TrackedItem>? trackedItems = null,
        IReadOnlyList<int>? excludedIds = null)
    {
        return new Workspace(context, sprintItems, seeds, hierarchy, sections, trackedItems, excludedIds);
    }

    /// <summary>
    /// Returns true if the given work item ID is in the tracked items collection.
    /// </summary>
    public bool IsTracked(int workItemId)
    {
        foreach (var t in TrackedItems)
        {
            if (t.WorkItemId == workItemId)
                return true;
        }
        return false;
    }
}
