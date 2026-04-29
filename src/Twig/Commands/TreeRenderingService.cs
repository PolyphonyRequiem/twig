using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Shared service that builds and renders a <see cref="WorkTree"/> hierarchy.
/// Extracted from <see cref="TreeCommand"/> so that <c>ShowCommand --tree</c>
/// and <c>WorkspaceCommand --tree</c> can reuse the same rendering logic.
/// </summary>
public sealed class TreeRenderingService(
    CommandContext ctx,
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinatorFactory syncCoordinatorFactory,
    IProcessTypeStore processTypeStore)
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

        if (fmt is HumanOutputFormatter humanFmt)
        {
            var treeProcessConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            if (treeProcessConfig is not null)
            {
                var typeLevelMap = BacklogHierarchyService.GetTypeLevelMap(treeProcessConfig);
                var parentChildMap = BacklogHierarchyService.InferParentChildMap(treeProcessConfig);
                Console.WriteLine(humanFmt.FormatTree(tree, maxDepth, activeId, typeLevelMap, parentChildMap, ctx.Config.Workspace.WorkingLevel));
            }
            else
            {
                Console.WriteLine(fmt.FormatTree(tree, maxDepth, activeId));
            }
        }
        else
        {
            Console.WriteLine(fmt.FormatTree(tree, maxDepth, activeId));
        }

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
}
