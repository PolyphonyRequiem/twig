using System.Diagnostics;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Shared service that builds and renders a <see cref="WorkTree"/> hierarchy.
/// Extracted from the former <c>TreeCommand</c> so that <c>ShowCommand --tree</c>
/// and <c>WorkspaceCommand --tree</c> can reuse the same rendering logic.
/// </summary>
public sealed class TreeRenderingService(
    CommandContext ctx,
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinatorFactory syncCoordinatorFactory,
    IProcessTypeStore processTypeStore,
    RendererFactory rendererFactory)
{
    /// <summary>
    /// Builds and renders a work-item tree, matching the behaviour of <c>twig tree</c>.
    /// </summary>
    /// <param name="id">Explicit work item ID, or <c>null</c> to use the active item.</param>
    /// <param name="outputFormat">Output format name (human, json, minimal, etc.).</param>
    /// <param name="depth">Maximum tree depth, or <c>null</c> for the configured default.</param>
    /// <param name="noLive">When <c>true</c>, disables live/async rendering.</param>
    /// <param name="noRefresh">When <c>true</c>, skips the background sync pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code: 0 on success, 1 on failure.</returns>
    public async Task<int> RenderTreeAsync(
        int? id,
        string outputFormat,
        int? depth,
        bool noLive,
        bool noRefresh,
        CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat, noLive);

        var activeId = id ?? await contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        var maxDepth = depth ?? ctx.Config.Display.TreeDepth;

        // Resolve active item with auto-fetch on cache miss
        var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value);
        if (!resolveResult.TryGetWorkItem(out var resolvedItem, out _, out _))
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        if (renderer is not null)
        {
            // Load process config for unparented banner
            var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            var spectreRenderer = renderer as SpectreRenderer;
            if (spectreRenderer is not null && processConfig is not null)
            {
                spectreRenderer.TypeLevelMap = BacklogHierarchyService.GetTypeLevelMap(processConfig);
                spectreRenderer.ParentChildMap = BacklogHierarchyService.InferParentChildMap(processConfig);
                spectreRenderer.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;
            }

            var getSiblingCount = MakeSiblingCounter(resolvedItem, ct);

            // Fallback: render tree without sync
            Task RenderTreeDirectAsync() => renderer.RenderTreeAsync(
                getFocusedItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(resolvedItem),
                getParentChain: async () => resolvedItem?.ParentId is null
                    ? Array.Empty<Domain.Aggregates.WorkItem>()
                    : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value, ct),
                getChildren: () => workItemRepo.GetChildrenAsync(activeId.Value, ct),
                maxDepth: maxDepth,
                activeId: activeId,
                ct: ct,
                getSiblingCount: getSiblingCount,
                getLinks: async () =>
                {
                    try { return await syncCoordinatorFactory.ReadOnly.SyncLinksAsync(resolvedItem.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<WorkItemLink>(); }
                });

            if (spectreRenderer is not null && !noRefresh)
            {
                try
                {
                    var cachedParentChain = resolvedItem.ParentId is null
                        ? Array.Empty<Domain.Aggregates.WorkItem>()
                        : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value, ct);
                    var cachedChildren = await workItemRepo.GetChildrenAsync(activeId.Value, ct);

                    IReadOnlyList<WorkItemLink> cachedLinks = Array.Empty<WorkItemLink>();
                    try { cachedLinks = await syncCoordinatorFactory.ReadOnly.SyncLinksAsync(resolvedItem.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                    var workingSet = await workingSetService.ComputeAsync([resolvedItem.IterationPath]);

                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => spectreRenderer.BuildTreeViewAsync(
                            resolvedItem,
                            cachedParentChain,
                            cachedChildren,
                            maxDepth,
                            activeId,
                            getSiblingCount,
                            cachedLinks,
                            ctx.Config.Display.CacheStaleMinutes),
                        performSync: () => syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(workingSet),
                        buildRevisedView: async _ =>
                        {
                            var freshItem = await workItemRepo.GetByIdAsync(resolvedItem.Id, CancellationToken.None);
                            if (freshItem is null) return null;

                            var freshParentChain = freshItem.ParentId is null
                                ? Array.Empty<Domain.Aggregates.WorkItem>()
                                : await workItemRepo.GetParentChainAsync(freshItem.ParentId.Value, CancellationToken.None);
                            var freshChildren = await workItemRepo.GetChildrenAsync(freshItem.Id, CancellationToken.None);

                            return await spectreRenderer.BuildTreeViewAsync(
                                freshItem,
                                freshParentChain,
                                freshChildren,
                                maxDepth,
                                activeId,
                                MakeSiblingCounter(freshItem, CancellationToken.None),
                                cachedLinks,
                                ctx.Config.Display.CacheStaleMinutes);
                        },
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    await RenderTreeDirectAsync();
                }
            }
            else
            {
                await RenderTreeDirectAsync();
            }

            return 0;
        }

        // Non-TTY path: JSON, minimal, piped output
        var item = resolvedItem;

        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);

        var descendantsByParentId = new Dictionary<int, IReadOnlyList<Domain.Aggregates.WorkItem>>();
        await WorkTreeFetcher.FetchDescendantsAsync(workItemRepo, children, maxDepth - 1, descendantsByParentId, ct);

        var siblingCounts = new Dictionary<int, int?>();
        foreach (var node in parentChain)
        {
            if (node.ParentId.HasValue)
            {
                var siblings = await workItemRepo.GetChildrenAsync(node.ParentId.Value);
                siblingCounts[node.Id] = siblings.Count;
            }
            else
            {
                siblingCounts[node.Id] = null;
            }
        }
        if (item.ParentId.HasValue)
        {
            var focusedSiblings = await workItemRepo.GetChildrenAsync(item.ParentId.Value);
            siblingCounts[item.Id] = focusedSiblings.Count;
        }
        else
        {
            siblingCounts[item.Id] = null;
        }

        IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
        try
        {
            links = await syncCoordinatorFactory.ReadOnly.SyncLinksAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var tree = WorkTree.Build(item, parentChain, children, siblingCounts, links, descendantsByParentId);

        RenderWorkTree(tree, outputFormat);

        // Sync working set silently after output — best-effort; skip if --no-refresh
        if (!noRefresh)
        {
            try
            {
                var syncWorkingSet = await workingSetService.ComputeAsync([item.IterationPath]);
                await syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(syncWorkingSet);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* sync is best-effort */ }
        }

        return 0;
    }

    /// <summary>
    /// Factory: build a sibling-count resolver for a given root item.
    /// </summary>
    private Func<int, Task<int?>> MakeSiblingCounter(Domain.Aggregates.WorkItem root, CancellationToken token) =>
        async nodeId =>
        {
            var parentId = nodeId == root.Id ? root.ParentId
                : (await workItemRepo.GetByIdAsync(nodeId, token))?.ParentId;
            if (!parentId.HasValue) return null;
            return (await workItemRepo.GetChildrenAsync(parentId.Value, token)).Count;
        };

    private void RenderWorkTree(WorkTree tree, string outputFormat)
    {
        var fields = new List<DocumentField>
        {
            new("parentChain", new RenderNode.Section(null,
                tree.ParentChain.Select(p => (RenderNode)BuildWorkItemRecord(p)).ToList())),
            new("focus", BuildWorkItemRecord(tree.FocusedItem)),
            new("children", new RenderNode.Section(null,
                tree.Children.Select(c => (RenderNode)new RenderNode.TreeView(BuildBranch(c, tree))).ToList())),
            new("totalChildren", new RenderNode.KeyValue("totalChildren", RenderCell.Integer(tree.Children.Count))),
            new("links", new RenderNode.Section(null,
                tree.FocusedItemLinks.Select(BuildLinkRecord).ToList())),
        };

        var doc = new RenderNode.Document(null, fields);
        var rt = new Twig.RenderTree.RenderTree([doc]);
        rendererFactory.GetRenderer(outputFormat).Render(rt);
        Console.WriteLine();
    }

    private static RenderNode.Record BuildWorkItemRecord(Domain.Aggregates.WorkItem item)
    {
        var tags = string.Empty;
        if (item.Fields.TryGetValue("System.Tags", out var t) && t is not null)
            tags = t;

        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(item.Id),
            ["title"] = RenderCell.String(item.Title ?? string.Empty),
            ["type"] = RenderCell.String(item.Type.ToString()),
            ["state"] = RenderCell.String(item.State ?? string.Empty),
            ["assignedTo"] = RenderCell.String(item.AssignedTo ?? string.Empty),
            ["isDirty"] = RenderCell.Boolean(item.IsDirty),
            ["isSeed"] = RenderCell.Boolean(item.IsSeed),
            ["parentId"] = item.ParentId.HasValue
                ? RenderCell.Integer(item.ParentId.Value)
                : new RenderCell(string.Empty, new RenderValue.Null()),
            ["tags"] = RenderCell.String(tags),
        };

        // Mirror ShowCommand's `fields` block on each tree node so polyphony
        // (and other JSON consumers) can read per-node System.Description,
        // System.Tags, or any other ADO field directly from the tree output.
        var fieldsBlock = BuildNodeFieldsBlock(item);
        if (fieldsBlock is not null)
            cells["fields"] = new RenderCell(string.Empty, new RenderValue.Object(fieldsBlock));

        return new RenderNode.Record("workItem", cells);
    }

    private static IReadOnlyDictionary<string, RenderCell>? BuildNodeFieldsBlock(Domain.Aggregates.WorkItem item)
    {
        if (item.Fields.Count == 0)
            return null;

        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal);
        foreach (var (refName, value) in item.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            // Tags ARE included here — polyphony reads `fields["System.Tags"]`
            // as a fallback alongside the top-level `tags` projection.
            cells[refName] = RenderCell.String(value);
        }

        return cells.Count == 0 ? null : cells;
    }

    private static RenderTreeBranch BuildBranch(Domain.Aggregates.WorkItem item, WorkTree tree)
    {
        var record = BuildWorkItemRecord(item);
        var row = new RenderRow(null, record.Fields);
        var descendants = tree.GetDescendants(item.Id);
        var childBranches = descendants.Select(d => BuildBranch(d, tree)).ToList();
        return new RenderTreeBranch(row, childBranches);
    }

    private static RenderNode BuildLinkRecord(WorkItemLink link)
    {
        return new RenderNode.Record("workItemLink", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["sourceId"] = RenderCell.Integer(link.SourceId),
            ["targetId"] = RenderCell.Integer(link.TargetId),
            ["linkType"] = RenderCell.String(link.LinkType ?? string.Empty),
        });
    }
}
