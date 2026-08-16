using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Immutable read model for a SET of work items and the non-hierarchy edges among them.
/// </summary>
/// <remarks>
/// <para>
/// This is the <em>graph</em> read model. <see cref="WorkTree"/> is the <em>hierarchy</em>
/// read model and stays that way: it has a focused item, a parent chain, and children, and
/// its <see cref="WorkTree.FocusedItemLinks"/> belong to that one focused item.
/// </para>
/// <para>
/// 🔴 The distinction that motivates a separate type (ADO #154): <b>a link is an edge between
/// two items, so it belongs to the SET, not to a member of it.</b> Every read path above the
/// <c>work_item_links</c> table historically narrowed to a single id — repository, ADO client,
/// sync, and read model each repeated the same assumption — which is why a set-reading consumer
/// could not ask for the edges among its items without issuing one call per item and stitching
/// the answers together itself.
/// </para>
/// <para>
/// Widening <see cref="WorkTree"/> instead was considered and rejected on the card: it would
/// widen the model every single-item consumer uses in order to serve a consumer that does not
/// want a tree at all.
/// </para>
/// <para>
/// <b>Edges are not filtered to the set.</b> <see cref="Links"/> carries every edge whose
/// <see cref="WorkItemLink.SourceId"/> is a member of <see cref="Items"/>, including edges whose
/// target lies outside it. A consumer drawing only intra-set edges should filter with
/// <see cref="ContainsItem"/>; a consumer discovering what to fetch next needs precisely the
/// edges that leave the set, and silently dropping them here would make that impossible.
/// </para>
/// </remarks>
public sealed class WorkItemGraph
{
    /// <summary>The work items in the set, in the order supplied by the caller.</summary>
    public IReadOnlyList<WorkItem> Items { get; }

    /// <summary>
    /// Every non-hierarchy edge (Related, Predecessor, Successor) sourced from an item in
    /// <see cref="Items"/>. Empty when no links exist or links were not fetched.
    /// </summary>
    public IReadOnlyList<WorkItemLink> Links { get; }

    private readonly Dictionary<int, IReadOnlyList<WorkItemLink>> _linksBySource;
    private readonly HashSet<int> _itemIds;

    private WorkItemGraph(
        IReadOnlyList<WorkItem> items,
        IReadOnlyList<WorkItemLink> links,
        Dictionary<int, IReadOnlyList<WorkItemLink>> linksBySource,
        HashSet<int> itemIds)
    {
        Items = items;
        Links = links;
        _linksBySource = linksBySource;
        _itemIds = itemIds;
    }

    /// <summary>
    /// Builds an immutable <see cref="WorkItemGraph"/> from a set of items and the edges
    /// among them. A null or omitted <paramref name="links"/> yields a graph with no edges,
    /// which is the correct shape for a set whose links have not been fetched.
    /// </summary>
    public static WorkItemGraph Build(
        IReadOnlyList<WorkItem> items,
        IReadOnlyList<WorkItemLink>? links = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var edges = links ?? Array.Empty<WorkItemLink>();

        var itemIds = new HashSet<int>();
        foreach (var item in items)
            itemIds.Add(item.Id);

        var grouped = new Dictionary<int, List<WorkItemLink>>();
        foreach (var link in edges)
        {
            if (!grouped.TryGetValue(link.SourceId, out var bucket))
            {
                bucket = [];
                grouped[link.SourceId] = bucket;
            }

            bucket.Add(link);
        }

        var bySource = new Dictionary<int, IReadOnlyList<WorkItemLink>>(grouped.Count);
        foreach (var kvp in grouped)
            bySource[kvp.Key] = kvp.Value;

        return new WorkItemGraph(items, edges, bySource, itemIds);
    }

    /// <summary>
    /// Returns the edges sourced from <paramref name="workItemId"/>, or an empty list when
    /// that item has none. Never throws for an id outside the set — an absent id and an id
    /// with no edges are both legitimately "no edges from here".
    /// </summary>
    public IReadOnlyList<WorkItemLink> GetLinks(int workItemId)
    {
        return _linksBySource.TryGetValue(workItemId, out var links)
            ? links
            : Array.Empty<WorkItemLink>();
    }

    /// <summary>True when <paramref name="workItemId"/> is a member of <see cref="Items"/>.</summary>
    public bool ContainsItem(int workItemId) => _itemIds.Contains(workItemId);
}
