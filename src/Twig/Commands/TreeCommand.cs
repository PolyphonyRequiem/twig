using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
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
    RenderingPipelineFactory? pipelineFactory = null)
{
    /// <summary>Display the work item hierarchy as a tree.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = "human", int? depth = null, bool all = false, bool noLive = false, CancellationToken ct = default)
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
            // Async progressive rendering path — delegates to SpectreRenderer.RenderTreeAsync.
            await renderer.RenderTreeAsync(
                getFocusedItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(resolvedItem),
                getParentChain: async () => resolvedItem?.ParentId is null
                    ? Array.Empty<Domain.Aggregates.WorkItem>()
                    : await workItemRepo.GetParentChainAsync(resolvedItem.ParentId.Value),
                getChildren: () => workItemRepo.GetChildrenAsync(activeId.Value),
                maxChildren: maxChildren,
                activeId: activeId,
                ct: CancellationToken.None);

            // Sync working set after cached render (EPIC-004) — best-effort
            try
            {
                var workingSet = await workingSetService.ComputeAsync(resolvedItem.IterationPath);
                await renderer.RenderWithSyncAsync(
                    buildCachedView: () =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable>(
                            new Spectre.Console.Text(string.Empty)),
                    performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet),
                    buildRevisedView: syncResult =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* sync is best-effort — don't fail the command */ }

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, piped output)
        var item = resolvedItem;

        // Build parent chain
        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);
        var tree = WorkTree.Build(item, parentChain, children);

        Console.WriteLine(fmt.FormatTree(tree, maxChildren, activeId));

        // Sync working set silently after output (EPIC-004) — best-effort
        try
        {
            var syncWorkingSet = await workingSetService.ComputeAsync(item.IterationPath);
            await syncCoordinator.SyncWorkingSetAsync(syncWorkingSet);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* sync is best-effort — don't fail the command */ }

        return 0;
    }
}
