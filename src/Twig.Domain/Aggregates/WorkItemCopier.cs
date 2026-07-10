using Twig.Domain.ValueObjects;

namespace Twig.Domain.Aggregates;

/// <summary>
/// Centralizes all <see cref="WorkItem"/> copy construction.
/// Every init-only property is explicitly transferred, so adding a new property
/// to <see cref="WorkItem"/> forces a compile error here until this method is updated.
/// </summary>
internal static class WorkItemCopier
{
    /// <summary>
    /// Creates a copy of <paramref name="source"/> with optional overrides.
    /// </summary>
    /// <param name="source">The work item to copy.</param>
    /// <param name="titleOverride">If non-null, replaces the source title.</param>
    /// <param name="overrideParentId">When <c>true</c>, uses <paramref name="parentIdValue"/>
    /// instead of <c>source.ParentId</c>. This flag distinguishes "set ParentId to null"
    /// from "don't change ParentId".</param>
    /// <param name="parentIdValue">The ParentId to use when <paramref name="overrideParentId"/> is <c>true</c>.</param>
    /// <param name="isSeedOverride">If non-null, replaces the source <see cref="WorkItem.IsSeed"/> flag.</param>
    /// <param name="fieldsOverride">If non-null, the field set to apply.</param>
    /// <param name="preserveExistingFields">When <c>true</c> and <paramref name="fieldsOverride"/>
    /// is provided, source fields are imported first and the override merges on top.
    /// When <c>false</c>, only <paramref name="fieldsOverride"/> fields are used.</param>
    /// <param name="preserveDirty">When <c>true</c>, the source dirty flag is transferred
    /// to the copy.</param>
    /// <param name="typeOverride">If non-null, replaces the source <see cref="WorkItem.Type"/>.</param>
    /// <param name="overrideAssignedTo">When <c>true</c>, uses <paramref name="assignedToValue"/>
    /// instead of <c>source.AssignedTo</c>.</param>
    /// <param name="assignedToValue">The assigned-to value when <paramref name="overrideAssignedTo"/> is <c>true</c>.</param>
    /// <param name="areaPathOverride">If non-null, replaces the source <see cref="WorkItem.AreaPath"/>.</param>
    /// <param name="iterationPathOverride">If non-null, replaces the source <see cref="WorkItem.IterationPath"/>.</param>
    internal static WorkItem Copy(
        WorkItem source,
        string? titleOverride = null,
        bool overrideParentId = false,
        int? parentIdValue = null,
        bool? isSeedOverride = null,
        IReadOnlyDictionary<string, string?>? fieldsOverride = null,
        bool preserveExistingFields = true,
        bool preserveDirty = false,
        WorkItemType? typeOverride = null,
        bool overrideAssignedTo = false,
        string? assignedToValue = null,
        AreaPath? areaPathOverride = null,
        IterationPath? iterationPathOverride = null)
    {
        var copy = new WorkItem
        {
            Id = source.Id,
            Type = typeOverride ?? source.Type,
            Title = titleOverride ?? source.Title,
            State = source.State,
            AssignedTo = overrideAssignedTo ? assignedToValue : source.AssignedTo,
            IterationPath = iterationPathOverride ?? source.IterationPath,
            AreaPath = areaPathOverride ?? source.AreaPath,
            ParentId = overrideParentId ? parentIdValue : source.ParentId,
            IsSeed = isSeedOverride ?? source.IsSeed,
            SeedCreatedAt = source.SeedCreatedAt,
            LastSyncedAt = source.LastSyncedAt,
        };

        if (source.Revision > 0)
            copy.MarkSynced(source.Revision);

        if (fieldsOverride is not null)
        {
            if (preserveExistingFields)
            {
                copy.ImportFields(source.Fields);
                copy.ImportFields(fieldsOverride);
            }
            else
            {
                copy.ImportFields(fieldsOverride);
            }
        }
        else
        {
            copy.ImportFields(source.Fields);
        }

        if (preserveDirty && source.IsDirty)
            copy.SetDirty();

        return copy;
    }
}
