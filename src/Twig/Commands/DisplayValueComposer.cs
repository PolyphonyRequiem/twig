using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Infrastructure.Persistence;

namespace Twig.Commands;

/// <summary>
/// Builds a display-only map of durable local field values for already-selected workspace rows
/// (#1186, decision A+R). The mirror workspace is never rebuilt: membership, selectors, ordering,
/// hierarchy, ceilings, state buckets, footer counts and assignee grouping all keep reading the
/// mirror items. Renderers consult <see cref="Snapshot"/> only for row cell text and colors, so
/// an edited State, Iteration or AssignedTo may show a value that would not itself satisfy the
/// Bench selector or the bucket it is rendered in. Seeds are #1187/#839 scope and never composed.
/// With no overlay, or nothing composed, <see cref="Snapshot"/> is null and legacy output is
/// byte-identical.
/// </summary>
internal sealed class DisplayValueComposer(DurableFieldOverlay? overlay)
{
    private readonly Dictionary<int, WorkItem> _shown = new();
    private readonly HashSet<WorkItem> _visited = new(ReferenceEqualityComparer.Instance);

    public async Task<WorkItem?> ItemAsync(WorkItem? item, CancellationToken ct)
    {
        if (item is null || overlay is null)
            return item;
        if (!_visited.Add(item))
            return _shown.TryGetValue(item.Id, out var cached) ? cached : item;

        var shown = await overlay.ApplyAsync(item, ct);
        if (ReferenceEquals(shown, item))
            _shown.Remove(item.Id);
        else
            _shown[item.Id] = shown;
        return shown;
    }

    public async Task AddItemsAsync(IEnumerable<WorkItem> items, CancellationToken ct)
    {
        if (overlay is null)
            return;
        foreach (var item in items)
            await ItemAsync(item, ct);
    }

    public async Task AddNodesAsync(IReadOnlyList<SprintHierarchyNode>? nodes, CancellationToken ct)
    {
        if (overlay is null || nodes is null)
            return;
        foreach (var node in nodes)
        {
            if (!node.IsVirtualGroup)
                await ItemAsync(node.Item, ct);
            await AddNodesAsync(node.Children, ct);
        }
    }

    public async Task AddSectionsAsync(WorkspaceSections? sections, CancellationToken ct)
    {
        if (overlay is null || sections is null)
            return;
        foreach (var section in sections.Sections)
        {
            await AddItemsAsync(section.Items, ct);
            await AddNodesAsync(section.TreeRoots, ct);
        }
    }

    public async Task AddWorkspaceAsync(Workspace workspace, CancellationToken ct)
    {
        if (overlay is null)
            return;
        await ItemAsync(workspace.ContextItem, ct);
        await AddItemsAsync(workspace.SprintItems, ct);
        if (workspace.Hierarchy is not null)
        {
            foreach (var roots in workspace.Hierarchy.AssigneeGroups.Values)
                await AddNodesAsync(roots, ct);
        }
        await AddSectionsAsync(workspace.Sections, ct);
    }

    /// <summary>Id → composed item for rows whose displayed values differ from the mirror; null when none.</summary>
    public IReadOnlyDictionary<int, WorkItem>? Snapshot() =>
        _shown.Count == 0 ? null : new Dictionary<int, WorkItem>(_shown);
}
