using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig tree</c>: builds a WorkTree and renders it with box-drawing characters.
/// After rendering cached tree, syncs the working set and revises if children/parent changed.
/// </summary>
public sealed class TreeCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinator syncCoordinator,
    IProcessTypeStore processTypeStore,
    RenderingPipelineFactory? pipelineFactory = null)
{
    /// <summary>Display the work item hierarchy as a tree.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat, noLive)
            : (formatterFactory.GetFormatter(outputFormat), null);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // ITEM-158: Resolve tree depth — --all overrides to show everything,
        // --depth <n> takes precedence, otherwise use config default.
        var maxChildren = all ? int.MaxValue
            : depth ?? config.Display.TreeDepth;

        // Resolve active item with auto-fetch on cache miss (G-3)
        var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value);
        if (!resolveResult.TryGetWorkItem(out var resolvedItem, out _, out _))
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        if (renderer is not null)
        {
            // EPIC-005: Load process config for unparented banner
            var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            if (renderer is SpectreRenderer spectreRenderer && processConfig is not null)
            {
                spectreRenderer.TypeLevelMap = BacklogHierarchyService.GetTypeLevelMap(processConfig);
                spectreRenderer.ParentChildMap = BacklogHierarchyService.InferParentChildMap(processConfig);
            }

            // Async progressive rendering path — delegates to SpectreRenderer.RenderTreeAsync.
            await renderer.RenderTreeAsync(
                getFocusedItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(resolvedItem),
                getParentChain: async () => resolvedItem?.ParentId is null
                    ? Array.Empty<Domain.Aggregates.WorkItem>()
                    : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value, ct),
                getChildren: () => workItemRepo.GetChildrenAsync(activeId.Value, ct),
                maxChildren: maxChildren,
                activeId: activeId,
                ct: ct,
                getSiblingCount: async (nodeId) =>
                {
                    // Find the node in the resolved item or its future parent chain
                    int? parentId = null;
                    if (resolvedItem is not null && resolvedItem.Id == nodeId)
                        parentId = resolvedItem.ParentId;
                    else
                    {
                        // Parent chain will be fetched by the renderer; look up from repo
                        var node = await workItemRepo.GetByIdAsync(nodeId, ct);
                        parentId = node?.ParentId;
                    }
                    if (!parentId.HasValue) return null;
                    var siblings = await workItemRepo.GetChildrenAsync(parentId.Value, ct);
                    return siblings.Count;
                },
                getLinks: async () =>
                {
                    try { return await syncCoordinator.SyncLinksAsync(resolvedItem.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return Array.Empty<WorkItemLink>(); }
                });

            // Sync working set after cached render (EPIC-004) — best-effort
            try
            {
                var workingSet = await workingSetService.ComputeAsync(resolvedItem.IterationPath);
                await renderer.RenderWithSyncAsync(
                    buildCachedView: () =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable>(
                            new Spectre.Console.Text(" ")),
                    performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet),
                    buildRevisedView: syncResult =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* sync is best-effort — don't fail the command */ }

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, piped output)
        var item = resolvedItem;

        // Build parent chain
        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);

        // Compute sibling counts for parent chain + focused item
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

        // Fetch related links (best-effort)
        IReadOnlyList<WorkItemLink> links = Array.Empty<WorkItemLink>();
        try
        {
            links = await syncCoordinator.SyncLinksAsync(item.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var tree = WorkTree.Build(item, parentChain, children, siblingCounts, links);

        // EPIC-005: Load process config for unparented banner
        if (fmt is HumanOutputFormatter humanFmt)
        {
            var treeProcessConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            if (treeProcessConfig is not null)
            {
                var typeLevelMap = BacklogHierarchyService.GetTypeLevelMap(treeProcessConfig);
                var parentChildMap = BacklogHierarchyService.InferParentChildMap(treeProcessConfig);
                Console.WriteLine(humanFmt.FormatTree(tree, maxChildren, activeId, typeLevelMap, parentChildMap));
            }
            else
            {
                Console.WriteLine(fmt.FormatTree(tree, maxChildren, activeId));
            }
        }
        else
        {
            Console.WriteLine(fmt.FormatTree(tree, maxChildren, activeId));
        }

        // Sync working set silently after output (EPIC-004) — best-effort
        try
        {
            var syncWorkingSet = await workingSetService.ComputeAsync(item.IterationPath);
            await syncCoordinator.SyncWorkingSetAsync(syncWorkingSet);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* sync is best-effort — don't fail the command */ }

        return 0;
    }
}
