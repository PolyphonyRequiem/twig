using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Domain service that constructs <see cref="WorkItem"/> aggregates from <see cref="WorkItemSnapshot"/>.
/// Owns all value object parsing and state restoration logic.
/// </summary>
public sealed class WorkItemMapper
{
    public WorkItem Map(WorkItemSnapshot snapshot)
    {
        var item = new WorkItem
        {
            Id = snapshot.Id,
            Type = ParseWorkItemType(snapshot.TypeName),
            Title = snapshot.Title,
            State = snapshot.State,
            AssignedTo = snapshot.AssignedTo,
            IterationPath = ParseIterationPath(snapshot.IterationPath),
            AreaPath = ParseAreaPath(snapshot.AreaPath),
            ParentId = snapshot.ParentId,
            IsSeed = snapshot.IsSeed,
            SeedCreatedAt = snapshot.SeedCreatedAt,
            LastSyncedAt = snapshot.LastSyncedAt,
        };

        if (snapshot.Revision > 0)
            item.MarkSynced(snapshot.Revision);

        item.ImportFields(snapshot.Fields);

        if (snapshot.IsDirty)
            item.SetDirty();

        return item;
    }

    private static WorkItemType ParseWorkItemType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return WorkItemType.Task;

        return WorkItemType.Parse(typeName).Value;
    }

    private static IterationPath ParseIterationPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return default;

        var result = IterationPath.Parse(raw);
        return result.IsSuccess ? result.Value : default;
    }

    private static AreaPath ParseAreaPath(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return default;

        var result = AreaPath.Parse(raw);
        return result.IsSuccess ? result.Value : default;
    }
}
