using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig tree</c>: builds a WorkTree and renders it with box-drawing characters.
/// </summary>
public sealed class TreeCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    // Optional — null for backward compat with tests that predate EPIC-003
    RenderingPipelineFactory? pipelineFactory = null)
{
    /// <summary>Display the work item hierarchy as a tree.</summary>
    public async Task<int> ExecuteAsync(string outputFormat = "human", int? depth = null, bool all = false, bool noLive = false)
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

        if (renderer is not null)
        {
            // Async progressive rendering path — delegates to SpectreRenderer.RenderTreeAsync.
            // Hoist the focused item fetch so getParentChain can close over item.ParentId
            // without a redundant GetByIdAsync round-trip.
            var capturedActiveId = activeId.Value;
            var focusedItem = await workItemRepo.GetByIdAsync(capturedActiveId);
            if (focusedItem is null)
            {
                Console.Error.WriteLine(fmt.FormatError($"Work item #{capturedActiveId} not found in cache."));
                return 1;
            }

            await renderer.RenderTreeAsync(
                getFocusedItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(focusedItem),
                getParentChain: async () => focusedItem?.ParentId is null
                    ? Array.Empty<Domain.Aggregates.WorkItem>()
                    : await workItemRepo.GetParentChainAsync(focusedItem.ParentId.Value),
                getChildren: () => workItemRepo.GetChildrenAsync(capturedActiveId),
                maxChildren: maxChildren,
                activeId: activeId,
                ct: CancellationToken.None);

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, piped output)
        var item = await workItemRepo.GetByIdAsync(activeId.Value);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        // Build parent chain
        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);
        var tree = WorkTree.Build(item, parentChain, children);

        Console.WriteLine(fmt.FormatTree(tree, maxChildren, activeId));

        return 0;
    }
}
