using System.Diagnostics;
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
    SyncCoordinatorFactory syncCoordinatorFactory,
    IProcessTypeStore processTypeStore,
    RenderingPipelineFactory? pipelineFactory = null,
    ITelemetryClient? telemetryClient = null)
{
    /// <summary>Display the work item hierarchy as a tree.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, int? depth = null, bool all = false, bool noLive = false, bool noRefresh = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, depth, all, noLive, noRefresh, ct);
        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "tree",
            ["exit_code"] = exitCode.ToString(),
            ["output_format"] = outputFormat,
            ["twig_version"] = VersionHelper.GetVersion(),
            ["os_platform"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription
        }, new Dictionary<string, double>
        {
            ["duration_ms"] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds
        });
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(string outputFormat, int? depth, bool all, bool noLive, bool noRefresh, CancellationToken ct)
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
            var spectreRenderer = renderer as SpectreRenderer;
            if (spectreRenderer is not null && processConfig is not null)
            {
                spectreRenderer.TypeLevelMap = BacklogHierarchyService.GetTypeLevelMap(processConfig);
                spectreRenderer.ParentChildMap = BacklogHierarchyService.InferParentChildMap(processConfig);
            }

            // Shared factory: build a sibling-count resolver for any root item and token
            Func<int, Task<int?>> MakeSiblingCounter(Domain.Aggregates.WorkItem root, CancellationToken token) =>
                async nodeId =>
                {
                    var parentId = nodeId == root.Id ? root.ParentId
                        : (await workItemRepo.GetByIdAsync(nodeId, token))?.ParentId;
                    if (!parentId.HasValue) return null;
                    return (await workItemRepo.GetChildrenAsync(parentId.Value, token)).Count;
                };

            var getSiblingCount = MakeSiblingCounter(resolvedItem, ct);

            // Fallback: render tree without sync (--no-refresh, non-Spectre renderers, sync failures)
            Task RenderTreeDirectAsync() => renderer.RenderTreeAsync(
                getFocusedItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(resolvedItem),
                getParentChain: async () => resolvedItem?.ParentId is null
                    ? Array.Empty<Domain.Aggregates.WorkItem>()
                    : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value, ct),
                getChildren: () => workItemRepo.GetChildrenAsync(activeId.Value, ct),
                maxChildren: maxChildren,
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
                // Two-pass rendering: build tree from cache → sync → rebuild from fresh data
                try
                {
                    var cachedParentChain = resolvedItem.ParentId is null
                        ? Array.Empty<Domain.Aggregates.WorkItem>()
                        : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value, ct);
                    var cachedChildren = await workItemRepo.GetChildrenAsync(activeId.Value, ct);

                    IReadOnlyList<WorkItemLink> cachedLinks = Array.Empty<WorkItemLink>();
                    try { cachedLinks = await syncCoordinatorFactory.ReadOnly.SyncLinksAsync(resolvedItem.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                    var workingSet = await workingSetService.ComputeAsync(resolvedItem.IterationPath);

                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => spectreRenderer.BuildTreeViewAsync(
                            resolvedItem,
                            cachedParentChain,
                            cachedChildren,
                            maxChildren,
                            activeId,
                            getSiblingCount,
                            cachedLinks,
                            config.Display.CacheStaleMinutes),
                        performSync: () => syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(workingSet),
                        buildRevisedView: async _ =>
                        {
                            // Rebuild tree from fresh cache data after sync completes
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
                                maxChildren,
                                activeId,
                                MakeSiblingCounter(freshItem, CancellationToken.None),
                                cachedLinks,
                                config.Display.CacheStaleMinutes);
                        },
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Sync failure — fallback to direct tree render without sync
                    await RenderTreeDirectAsync();
                }
            }
            else
            {
                // --no-refresh or non-Spectre renderer: render tree directly, no sync
                await RenderTreeDirectAsync();
            }

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
            links = await syncCoordinatorFactory.ReadOnly.SyncLinksAsync(item.Id, ct);
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

        // Sync working set silently after output (EPIC-004) — best-effort; skip if --no-refresh
        if (!noRefresh)
        {
            try
            {
                var syncWorkingSet = await workingSetService.ComputeAsync(item.IterationPath);
                await syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(syncWorkingSet);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { /* sync is best-effort — don't fail the command */ }
        }

        return 0;
    }
}
