using Twig.Domain.Aggregates;

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

    private Workspace(WorkItem? context, IReadOnlyList<WorkItem> sprintItems, IReadOnlyList<WorkItem> seeds, SprintHierarchy? hierarchy, WorkspaceSections? sections)
    {
        ContextItem = context;
        SprintItems = sprintItems;
        Seeds = seeds;
        Hierarchy = hierarchy;
        Sections = sections;
    }

    /// <summary>
    /// Builds an immutable <see cref="Workspace"/> from context, sprint, and seed items.
    /// </summary>
    public static Workspace Build(WorkItem? context, IReadOnlyList<WorkItem> sprintItems, IReadOnlyList<WorkItem> seeds, SprintHierarchy? hierarchy = null, WorkspaceSections? sections = null)
    {
        return new Workspace(context, sprintItems, seeds, hierarchy, sections);
    }

    /// <summary>
    /// Returns seeds whose <see cref="WorkItem.SeedCreatedAt"/> is older than <paramref name="thresholdDays"/>.
    /// </summary>
    public IReadOnlyList<WorkItem> GetStaleSeeds(int thresholdDays)
    {
        var threshold = DateTimeOffset.UtcNow.AddDays(-thresholdDays);
        var stale = new List<WorkItem>();

        foreach (var seed in Seeds)
        {
            if (seed.SeedCreatedAt.HasValue && seed.SeedCreatedAt.Value < threshold)
                stale.Add(seed);
        }

        return stale;
    }

    /// <summary>
    /// Returns all dirty items from sprint items and seeds.
    /// </summary>
    public IReadOnlyList<WorkItem> GetDirtyItems()
    {
        var dirty = new List<WorkItem>();

        foreach (var item in SprintItems)
        {
            if (item.IsDirty)
                dirty.Add(item);
        }

        foreach (var seed in Seeds)
        {
            if (seed.IsDirty)
                dirty.Add(seed);
        }

        return dirty;
    }

    /// <summary>
    /// Returns a deduplicated union of context item, sprint items, and seeds (by ID).
    /// </summary>
    public IReadOnlyList<WorkItem> ListAll()
    {
        var seen = new HashSet<int>();
        var result = new List<WorkItem>();

        if (ContextItem is not null && seen.Add(ContextItem.Id))
            result.Add(ContextItem);

        foreach (var item in SprintItems)
        {
            if (seen.Add(item.Id))
                result.Add(item);
        }

        foreach (var seed in Seeds)
        {
            if (seen.Add(seed.Id))
                result.Add(seed);
        }

        return result;
    }
}
