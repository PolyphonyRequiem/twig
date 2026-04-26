using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Pure computation methods extracted from <see cref="Workspace"/>.
/// Keeps the read model as an inert projection while providing
/// filtering, deduplication, and threshold calculations as extensions.
/// </summary>
public static class WorkspaceExtensions
{
    /// <summary>
    /// Returns seeds whose <see cref="WorkItem.SeedCreatedAt"/> is older than <paramref name="thresholdDays"/>.
    /// </summary>
    public static IReadOnlyList<WorkItem> GetStaleSeeds(this Workspace workspace, int thresholdDays)
    {
        var threshold = DateTimeOffset.UtcNow.AddDays(-thresholdDays);
        var stale = new List<WorkItem>();

        foreach (var seed in workspace.Seeds)
        {
            if (seed.SeedCreatedAt.HasValue && seed.SeedCreatedAt.Value < threshold)
                stale.Add(seed);
        }

        return stale;
    }

    /// <summary>
    /// Returns all dirty items from sprint items and seeds.
    /// </summary>
    public static IReadOnlyList<WorkItem> GetDirtyItems(this Workspace workspace)
    {
        var dirty = new List<WorkItem>();

        foreach (var item in workspace.SprintItems)
        {
            if (item.IsDirty)
                dirty.Add(item);
        }

        foreach (var seed in workspace.Seeds)
        {
            if (seed.IsDirty)
                dirty.Add(seed);
        }

        return dirty;
    }

    /// <summary>
    /// Returns a deduplicated union of context item, sprint items, and seeds (by ID).
    /// </summary>
    public static IReadOnlyList<WorkItem> ListAll(this Workspace workspace)
    {
        var seen = new HashSet<int>();
        var result = new List<WorkItem>();

        if (workspace.ContextItem is not null && seen.Add(workspace.ContextItem.Id))
            result.Add(workspace.ContextItem);

        foreach (var item in workspace.SprintItems)
        {
            if (seen.Add(item.Id))
                result.Add(item);
        }

        foreach (var seed in workspace.Seeds)
        {
            if (seen.Add(seed.Id))
                result.Add(seed);
        }

        return result;
    }
}
