using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Builds a <see cref="SprintHierarchy"/> from flat sprint items by walking parent chains,
/// grouping by assignee, and creating virtual group headers for unparented items.
/// </summary>
public interface ISprintHierarchyBuilder
{
    /// <summary>
    /// Builds a <see cref="SprintHierarchy"/> from flat sprint items, a parent lookup, and
    /// an optional ceiling type name list that trims parent chains.
    /// </summary>
    /// <param name="sprintItems">Work items present in the sprint.</param>
    /// <param name="parentLookup">Maps work item ID to parent work item for chain walking.</param>
    /// <param name="ceilingTypeNames">
    /// The type names at which to stop walking the parent chain (exclusive).
    /// When <c>null</c>, no parent context is added and items appear flat.
    /// </param>
    /// <param name="typeLevelMap">
    /// Optional map from work item type name to backlog level.
    /// When provided, unparented root items are grouped under virtual headers.
    /// </param>
    SprintHierarchy Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyDictionary<int, WorkItem> parentLookup,
        IReadOnlyList<string>? ceilingTypeNames,
        IReadOnlyDictionary<string, int>? typeLevelMap = null);
}