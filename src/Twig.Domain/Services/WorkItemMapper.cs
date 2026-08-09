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
        var assignedTo = GetCanonicalField(snapshot, "System.AssignedTo", snapshot.AssignedTo);
        var iterationPath = GetCanonicalField(snapshot, "System.IterationPath", snapshot.IterationPath);
        var areaPath = GetCanonicalField(snapshot, "System.AreaPath", snapshot.AreaPath);

        var item = new WorkItem
        {
            Id = snapshot.Id,
            Type = ParseWorkItemType(snapshot.TypeName),
            Title = snapshot.Title,
            State = snapshot.State,
            AssignedTo = assignedTo,
            IterationPath = ParseIterationPath(iterationPath),
            AreaPath = ParseAreaPath(areaPath),
            ParentId = snapshot.ParentId,
            IsSeed = snapshot.IsSeed,
            SeedCreatedAt = snapshot.SeedCreatedAt,
            StagedIdentity = ValueObjects.StagedIdentity.TryParse(snapshot.StagedIdentity, out var stagedIdentity)
                ? stagedIdentity
                : null,
            LastSyncedAt = snapshot.LastSyncedAt,
        };

        if (snapshot.Revision > 0)
            item.MarkSynced(snapshot.Revision);

        item.ImportFields(snapshot.Fields);

        if (snapshot.IsDirty)
            item.SetDirty();

        return item;
    }

    /// <summary>
    /// Projects a <see cref="WorkItem"/> aggregate back to the <see cref="WorkItemSnapshot"/>
    /// boundary type.
    /// </summary>
    /// <remarks>
    /// <see cref="Projections.WorkItemDetailProjector"/> takes a snapshot, not an aggregate
    /// (wayfinder ticket 0001 §2: read-only construction must not depend on aggregate
    /// behaviour or a persistence store). A host that already holds an aggregate — Twig's
    /// own TUI does — needs this direction to reach the projection without re-reading from
    /// the repository.
    /// </remarks>
    public WorkItemSnapshot ToSnapshot(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new WorkItemSnapshot
        {
            Id = item.Id,
            Revision = item.Revision,
            TypeName = item.Type.ToString(),
            Title = item.Title,
            State = item.State,
            AssignedTo = item.AssignedTo,
            IterationPath = item.IterationPath.ToString(),
            AreaPath = item.AreaPath.ToString(),
            ParentId = item.ParentId,
            IsSeed = item.IsSeed,
            SeedCreatedAt = item.SeedCreatedAt,
            StagedIdentity = item.StagedIdentity?.ToString(),
            LastSyncedAt = item.LastSyncedAt,
            IsDirty = item.IsDirty,
            Fields = item.Fields,
        };
    }

    private static string? GetCanonicalField(
        WorkItemSnapshot snapshot,
        string referenceName,
        string? fallback) =>
        snapshot.Fields.TryGetValue(referenceName, out var value) ? value : fallback;

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
