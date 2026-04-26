using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// A node in the sprint hierarchy tree. Wraps a <see cref="WorkItem"/> with
/// a flag indicating whether the item is actually in the sprint (vs. parent context only)
/// and an ordered list of child nodes.
/// Virtual group nodes (<see cref="IsVirtualGroup"/>=true) represent "Unparented [Types]"
/// section headers - they hold children but have no <see cref="WorkItem"/>.
/// </summary>
public sealed class SprintHierarchyNode
{
    /// <summary>
    /// The work item this node represents. Null for virtual group nodes.
    /// Always check <see cref="IsVirtualGroup"/> before accessing.
    /// </summary>
    public WorkItem Item { get; } = null!;
    public bool IsSprintItem { get; internal set; }
    public List<SprintHierarchyNode> Children { get; } = new();

    /// <summary>True when this node is a virtual section header for unparented items.</summary>
    public bool IsVirtualGroup { get; }

    /// <summary>Display label for virtual groups (e.g., "Unparented Features"). Null for real items.</summary>
    public string? GroupLabel { get; }

    /// <summary>Backlog level for virtual groups, relative to the tree's root level (0 = same as root, 1 = one level deeper, etc.).</summary>
    public int BacklogLevel { get; }

    internal SprintHierarchyNode(WorkItem item, bool isSprintItem)
    {
        Item = item;
        IsSprintItem = isSprintItem;
    }

    internal SprintHierarchyNode(string groupLabel, int backlogLevel)
    {
        IsVirtualGroup = true;
        GroupLabel = groupLabel;
        BacklogLevel = backlogLevel;
    }
}

/// <summary>
/// Immutable read model that organises sprint items into per-assignee hierarchical trees.
/// This is an inert data container - build logic lives in
/// <see cref="Twig.Domain.Services.SprintHierarchyBuilder"/>.
/// </summary>
public sealed class SprintHierarchy
{
    public IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> AssigneeGroups { get; }

    private SprintHierarchy(IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> assigneeGroups)
    {
        AssigneeGroups = assigneeGroups;
    }

    /// <summary>
    /// Creates a new <see cref="SprintHierarchy"/> from pre-built assignee groups.
    /// Used by <see cref="Twig.Domain.Services.SprintHierarchyBuilder"/>.
    /// </summary>
    public static SprintHierarchy Create(IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> assigneeGroups)
    {
        return new SprintHierarchy(assigneeGroups);
    }
}