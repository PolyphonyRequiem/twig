using Twig.Domain.Aggregates;
using Twig.Domain.Common;

namespace Twig.Domain.ReadModels;

/// <summary>
/// A node in the sprint hierarchy tree. Wraps a <see cref="WorkItem"/> with
/// a flag indicating whether the item is actually in the sprint (vs. parent context only)
/// and an ordered list of child nodes.
/// Virtual group nodes (<see cref="IsVirtualGroup"/>=true) represent "Unparented [Types]"
/// section headers — they hold children but have no <see cref="WorkItem"/>.
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

    /// <summary>Backlog level for virtual groups (0 = top portfolio, 1 = requirement, 2 = task).</summary>
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
/// Parent context is walked via <c>parentLookup</c> up to a ceiling type and deduplicated
/// within each assignee group.
/// </summary>
public sealed class SprintHierarchy
{
    public IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> AssigneeGroups { get; }

    private SprintHierarchy(IReadOnlyDictionary<string, IReadOnlyList<SprintHierarchyNode>> assigneeGroups)
    {
        AssigneeGroups = assigneeGroups;
    }

    /// <summary>
    /// Builds a <see cref="SprintHierarchy"/> from flat sprint items, a parent lookup, and
    /// an optional ceiling type name list that trims parent chains.
    /// </summary>
    /// <param name="sprintItems">Work items present in the sprint.</param>
    /// <param name="parentLookup">Maps work item ID → parent work item for chain walking.</param>
    /// <param name="ceilingTypeNames">
    /// The type names at which to stop walking the parent chain (exclusive — a ceiling
    /// type itself becomes a context node, but its parents are not included).
    /// When <c>null</c>, no parent context is added and items appear flat.
    /// </param>
    /// <param name="typeLevelMap">
    /// Optional map from work item type name → backlog level (0 = top portfolio, 1 = requirement, 2 = task).
    /// When provided, unparented root items are grouped under virtual "Unparented [Types]" headers.
    /// </param>
    public static SprintHierarchy Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyDictionary<int, WorkItem> parentLookup,
        IReadOnlyList<string>? ceilingTypeNames,
        IReadOnlyDictionary<string, int>? typeLevelMap = null)
    {
        if (sprintItems.Count == 0)
        {
            return new SprintHierarchy(new Dictionary<string, IReadOnlyList<SprintHierarchyNode>>());
        }

        // Track which IDs are sprint items for IsSprintItem marking
        var sprintItemIds = new HashSet<int>();
        foreach (var item in sprintItems)
            sprintItemIds.Add(item.Id);

        // Group sprint items by assignee (same convention as FormatSprintView)
        var grouped = new SortedDictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sprintItems)
        {
            var assignee = item.AssignedTo ?? "(unassigned)";
            if (!grouped.TryGetValue(assignee, out var list))
            {
                list = new List<WorkItem>();
                grouped[assignee] = list;
            }
            list.Add(item);
        }

        var result = new SortedDictionary<string, IReadOnlyList<SprintHierarchyNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (assignee, items) in grouped)
        {
            result[assignee] = BuildAssigneeTree(items, sprintItemIds, parentLookup, ceilingTypeNames, typeLevelMap);
        }

        return new SprintHierarchy(result);
    }

    private static IReadOnlyList<SprintHierarchyNode> BuildAssigneeTree(
        List<WorkItem> items,
        HashSet<int> sprintItemIds,
        IReadOnlyDictionary<int, WorkItem> parentLookup,
        IReadOnlyList<string>? ceilingTypeNames,
        IReadOnlyDictionary<string, int>? typeLevelMap)
    {
        // When no ceiling (null or empty), all items appear flat at root level
        if (ceilingTypeNames is null || ceilingTypeNames.Count == 0)
        {
            var flat = new List<SprintHierarchyNode>(items.Count);
            foreach (var item in items)
                flat.Add(new SprintHierarchyNode(item, isSprintItem: true));
            return flat;
        }

        // nodeById deduplicates shared parents within this assignee group
        var nodeById = new Dictionary<int, SprintHierarchyNode>();
        // Track parent→children relationships to build the tree
        var parentOf = new Dictionary<int, int>(); // childId → parentNodeId

        foreach (var item in items)
        {
            var node = GetOrCreateNode(nodeById, item, sprintItemIds);
            // If the item is a sprint item, ensure it's marked
            node.IsSprintItem = true;

            // Walk parent chain
            var current = item;
            while (current.ParentId.HasValue)
            {
                var parentId = current.ParentId.Value;

                if (!parentLookup.TryGetValue(parentId, out var parentItem))
                    break; // parent not in lookup → current stays at whatever level it is

                _ = GetOrCreateNode(nodeById, parentItem, sprintItemIds);

                // Record that current is a child of parent (if not already linked)
                if (!parentOf.ContainsKey(current.Id))
                    parentOf[current.Id] = parentItem.Id;

                // If this parent IS a ceiling type, stop walking (include it but go no higher)
                if (ceilingTypeNames.Any(t => string.Equals(parentItem.Type.Value, t, StringComparison.OrdinalIgnoreCase)))
                    break;

                current = parentItem;
            }
        }

        // Assemble children lists
        foreach (var (childId, parentId) in parentOf)
        {
            var parentNode = nodeById[parentId];
            var childNode = nodeById[childId];
            parentNode.Children.Add(childNode);
        }

        // Sort children within each parent node for deterministic ordering
        foreach (var (_, node) in nodeById)
        {
            if (node.Children.Count > 1)
                node.Children.Sort((a, b) => a.Item.Id.CompareTo(b.Item.Id));
        }

        // Root nodes are those that have no parent in parentOf, sorted by ID
        var childIds = new HashSet<int>(parentOf.Keys);
        var roots = new List<SprintHierarchyNode>();
        foreach (var (id, node) in nodeById)
        {
            if (!childIds.Contains(id))
                roots.Add(node);
        }

        roots.Sort((a, b) => a.Item.Id.CompareTo(b.Item.Id));

        // Partition roots into parented (proper subtree) and unparented (orphaned sprint items)
        if (typeLevelMap is null || typeLevelMap.Count == 0)
            return roots;

        var parented = new List<SprintHierarchyNode>();
        // Group unparented roots by backlog level for virtual grouping
        var unparentedByLevel = new SortedDictionary<int, List<SprintHierarchyNode>>();

        foreach (var root in roots)
        {
            // A root is "unparented" if it's a sprint item AND has no real parent
            if (root.IsSprintItem && !root.Item.ParentId.HasValue)
            {
                if (typeLevelMap.TryGetValue(root.Item.Type.Value, out var level))
                {
                    if (!unparentedByLevel.TryGetValue(level, out var group))
                    {
                        group = new List<SprintHierarchyNode>();
                        unparentedByLevel[level] = group;
                    }
                    group.Add(root);
                }
                else
                {
                    // Type not in level map — keep as normal root
                    parented.Add(root);
                }
            }
            else
            {
                parented.Add(root);
            }
        }

        // If no unparented items, return original roots unchanged
        if (unparentedByLevel.Count == 0)
            return roots;

        // Build result: parented roots first, then virtual groups sorted by level
        var result = new List<SprintHierarchyNode>(parented);

        foreach (var (level, group) in unparentedByLevel)
        {
            // Group by type name within the same level
            var typeGroups = new Dictionary<string, List<SprintHierarchyNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in group)
            {
                var typeName = node.Item.Type.Value;
                if (!typeGroups.TryGetValue(typeName, out var typeGroup))
                {
                    typeGroup = new List<SprintHierarchyNode>();
                    typeGroups[typeName] = typeGroup;
                }
                typeGroup.Add(node);
            }

            foreach (var (typeName, typeGroup) in typeGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var pluralName = Pluralizer.Pluralize(typeName);
                var virtualNode = new SprintHierarchyNode($"Unparented {pluralName}", level);
                virtualNode.Children.AddRange(typeGroup);
                result.Add(virtualNode);
            }
        }

        return result;
    }

    private static SprintHierarchyNode GetOrCreateNode(
        Dictionary<int, SprintHierarchyNode> nodeById,
        WorkItem item,
        HashSet<int> sprintItemIds)
    {
        if (!nodeById.TryGetValue(item.Id, out var node))
        {
            node = new SprintHierarchyNode(item, isSprintItem: sprintItemIds.Contains(item.Id));
            nodeById[item.Id] = node;
        }
        return node;
    }
}
