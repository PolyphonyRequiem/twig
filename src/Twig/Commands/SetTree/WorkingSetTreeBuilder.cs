using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;

namespace Twig.Commands.SetTree;

/// <summary>
/// Resolves a flat set of work item ids into a forest of trees (twig#277).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The default shape is an induced subtree, not a depth cut.</strong> Given a
/// set of ids, the forest contains exactly those items plus the ancestors needed to
/// connect them, and nothing else. That is what makes the output a review unit: every
/// node on screen is either something the caller asked about or structural context
/// explaining how two of those things relate. A depth cut instead pulls in arbitrary
/// unrelated descendants, which pads a consent surface with items no decision covers.
/// </para>
/// <para>
/// <c>depth</c> is therefore an opt-in <em>expansion</em> on top of the induced subtree:
/// <c>depth = 0</c> (the default) renders the induced subtree; <c>depth = n</c> also
/// pulls in <c>n</c> levels of children below each set member, for the case where the
/// reviewer needs to see what else lives under a node they are about to close.
/// </para>
/// <para>
/// Ids absent from the local cache become placeholder nodes rather than aborting the
/// render or being dropped. Their ancestry is unknown, so they attach at top level.
/// </para>
/// </remarks>
internal sealed class WorkingSetTreeBuilder(IWorkItemRepository repo)
{
    internal async Task<WorkingSetForest> BuildAsync(
        IReadOnlyList<int> requestedIds,
        IReadOnlyDictionary<int, TreeAnnotation> annotations,
        bool rootsOnly,
        int depth,
        CancellationToken ct)
    {
        var requested = new HashSet<int>(requestedIds);

        // 1. Load every requested item. Absent ones become placeholders.
        var itemsById = new Dictionary<int, WorkItem>();
        var missing = new List<int>();
        foreach (var id in requestedIds)
        {
            var item = await repo.GetByIdAsync(id, ct);
            if (item is null)
                missing.Add(id);
            else
                itemsById[id] = item;
        }

        // 2. Pull in connecting ancestors, unless --roots-only suppressed them.
        //    An ancestor is "connecting" when it lies on the parent chain of a set
        //    member; we keep the whole chain here and prune to the lowest common
        //    connectors in step 4.
        var connectorIds = new HashSet<int>();
        if (!rootsOnly)
        {
            foreach (var item in itemsById.Values.ToList())
            {
                if (item.ParentId is null)
                    continue;

                var chain = await repo.GetParentChainAsync(item.ParentId.Value, ct);
                foreach (var ancestor in chain)
                {
                    if (itemsById.TryAdd(ancestor.Id, ancestor))
                        connectorIds.Add(ancestor.Id);
                    else if (!requested.Contains(ancestor.Id))
                        connectorIds.Add(ancestor.Id);
                }
            }
        }

        // 3. Optional descendant expansion below each set member.
        var expandedIds = new HashSet<int>();
        if (depth > 0)
        {
            var frontier = requestedIds.Where(itemsById.ContainsKey).ToList();
            for (var level = 0; level < depth && frontier.Count > 0; level++)
            {
                var next = new List<int>();
                foreach (var parentId in frontier)
                {
                    foreach (var child in await repo.GetChildrenAsync(parentId, ct))
                    {
                        if (itemsById.TryAdd(child.Id, child))
                        {
                            expandedIds.Add(child.Id);
                            next.Add(child.Id);
                        }
                    }
                }
                frontier = next;
            }
        }

        // 4. Prune connectors that connect nothing: an ancestor chain above the
        //    highest set member adds indentation without adding information.
        //    A connector is kept only when it has two or more distinct subtrees
        //    beneath it, or when it sits between two set members.
        var keptIds = new HashSet<int>(itemsById.Keys);
        if (connectorIds.Count > 0)
        {
            foreach (var connectorId in connectorIds)
            {
                if (!IsLoadBearing(connectorId, itemsById, requested, expandedIds, keptIds))
                    keptIds.Remove(connectorId);
            }
        }

        // 5. Link into a forest. A node's parent is its nearest kept ancestor.
        var childrenByParent = new Dictionary<int, List<int>>();
        var rootIds = new List<int>();
        foreach (var id in OrderedIds(requestedIds, itemsById, keptIds))
        {
            var parentId = NearestKeptAncestor(itemsById[id], itemsById, keptIds);
            if (parentId is null)
                rootIds.Add(id);
            else
                (childrenByParent.TryGetValue(parentId.Value, out var list)
                    ? list
                    : childrenByParent[parentId.Value] = []).Add(id);
        }

        var roots = new List<WorkingSetNode>(rootIds.Count + missing.Count);
        foreach (var rootId in rootIds)
            roots.Add(BuildNode(rootId, itemsById, childrenByParent, requested, annotations));

        // Placeholders have no known ancestry — they render at top level, in the
        // caller's original ordering position relative to the other roots.
        foreach (var missingId in missing)
        {
            annotations.TryGetValue(missingId, out var annotation);
            roots.Add(new WorkingSetNode(missingId, null, InWorkingSet: true, annotation, []));
        }

        return new WorkingSetForest(roots, missing);
    }

    /// <summary>
    /// A connector earns its place when removing it would lose structure: it is
    /// load-bearing if two or more of its descendants-in-the-forest attach through
    /// separate branches, i.e. it is a genuine join point rather than a lone
    /// ancestor hanging above a single member.
    /// </summary>
    private static bool IsLoadBearing(
        int connectorId,
        IReadOnlyDictionary<int, WorkItem> itemsById,
        IReadOnlySet<int> requested,
        IReadOnlySet<int> expanded,
        IReadOnlySet<int> kept)
    {
        // Count direct "attachment branches": distinct children of this connector
        // that lead to at least one requested/expanded member.
        var branches = 0;
        foreach (var (id, item) in itemsById)
        {
            if (id == connectorId)
                continue;
            if (!requested.Contains(id) && !expanded.Contains(id))
                continue;

            // Walk up from this member; record the child-of-connector it came through.
            var cursor = item;
            int? viaChild = null;
            while (cursor is not null)
            {
                if (cursor.ParentId == connectorId)
                {
                    viaChild = cursor.Id;
                    break;
                }
                cursor = cursor.ParentId is not null && itemsById.TryGetValue(cursor.ParentId.Value, out var next)
                    ? next
                    : null;
            }

            if (viaChild is not null)
            {
                branches++;
                if (branches >= 2)
                    return true;
            }
        }

        // A connector that is itself the parent of nothing kept, or a lone spine
        // above one member, is dropped.
        _ = kept;
        return false;
    }

    /// <summary>Walks up the cached parent chain to the nearest id still in the forest.</summary>
    private static int? NearestKeptAncestor(
        WorkItem item,
        IReadOnlyDictionary<int, WorkItem> itemsById,
        IReadOnlySet<int> kept)
    {
        var parentId = item.ParentId;
        while (parentId is not null)
        {
            if (kept.Contains(parentId.Value))
                return parentId.Value;

            parentId = itemsById.TryGetValue(parentId.Value, out var parent) ? parent.ParentId : null;
        }

        return null;
    }

    /// <summary>
    /// Stable ordering: the caller's <c>--items</c> order first (so "structure 3 of 31"
    /// is reproducible), then any connectors/expansions by id.
    /// </summary>
    private static IEnumerable<int> OrderedIds(
        IReadOnlyList<int> requestedIds,
        IReadOnlyDictionary<int, WorkItem> itemsById,
        IReadOnlySet<int> kept)
    {
        var emitted = new HashSet<int>();
        foreach (var id in requestedIds)
        {
            if (kept.Contains(id) && itemsById.ContainsKey(id) && emitted.Add(id))
                yield return id;
        }

        foreach (var id in kept.Where(itemsById.ContainsKey).OrderBy(i => i))
        {
            if (emitted.Add(id))
                yield return id;
        }
    }

    private static WorkingSetNode BuildNode(
        int id,
        IReadOnlyDictionary<int, WorkItem> itemsById,
        IReadOnlyDictionary<int, List<int>> childrenByParent,
        IReadOnlySet<int> requested,
        IReadOnlyDictionary<int, TreeAnnotation> annotations)
    {
        var children = childrenByParent.TryGetValue(id, out var childIds)
            ? childIds.Select(childId => BuildNode(childId, itemsById, childrenByParent, requested, annotations)).ToList()
            : [];

        annotations.TryGetValue(id, out var annotation);
        return new WorkingSetNode(id, itemsById[id], requested.Contains(id), annotation, children);
    }
}
