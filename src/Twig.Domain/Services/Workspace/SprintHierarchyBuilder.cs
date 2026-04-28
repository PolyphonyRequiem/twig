using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Builds a <see cref="SprintHierarchy"/> from flat sprint items by walking parent chains,
/// grouping by assignee, and creating virtual group headers for unparented items.
/// </summary>
public sealed class SprintHierarchyBuilder : ISprintHierarchyBuilder
{
    /// <inheritdoc />
    public SprintHierarchy Build(
        IReadOnlyList<WorkItem> sprintItems,
        IReadOnlyDictionary<int, WorkItem> parentLookup,
        IReadOnlyList<string>? ceilingTypeNames,
        IReadOnlyDictionary<string, int>? typeLevelMap = null)
    {
        if (sprintItems.Count == 0)
        {
            return SprintHierarchy.Create(new Dictionary<string, IReadOnlyList<SprintHierarchyNode>>());
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

        return SprintHierarchy.Create(result);
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

        // Pre-compute ceiling type set for O(1) lookup instead of O(n) .Any()
        var ceilingTypeSet = new HashSet<string>(ceilingTypeNames, StringComparer.OrdinalIgnoreCase);

        // nodeById deduplicates shared parents within this assignee group
        var nodeById = new Dictionary<int, SprintHierarchyNode>();
        // Track parent->children relationships to build the tree
        var parentOf = new Dictionary<int, int>(); // childId -> parentNodeId

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
                    break; // parent not in lookup

                _ = GetOrCreateNode(nodeById, parentItem, sprintItemIds);

                // Record that current is a child of parent (if not already linked)
                if (!parentOf.ContainsKey(current.Id))
                    parentOf[current.Id] = parentItem.Id;

                // If this parent IS a ceiling type, stop walking (include it but go no higher)
                if (ceilingTypeSet.Contains(parentItem.Type.Value))
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
                    // Type not in level map - keep as normal root
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

        // Determine the root level so virtual-group BacklogLevel is stored as a
        // relative depth (matching the visual depth in the rendered tree).
        int rootLevel;
        if (parented.Count > 0)
        {
            var minLevel = int.MaxValue;
            foreach (var root in parented)
            {
                if (typeLevelMap.TryGetValue(root.Item.Type.Value, out var rl) && rl < minLevel)
                    minLevel = rl;
            }
            rootLevel = minLevel == int.MaxValue ? 0 : minLevel;
        }
        else
        {
            // SortedDictionary - first key is the minimum level.
            rootLevel = unparentedByLevel.Keys.First();
        }

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
                var virtualNode = new SprintHierarchyNode($"Unparented {pluralName}", level - rootLevel);
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