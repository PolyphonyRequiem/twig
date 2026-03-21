using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig up</c> and <c>twig down &lt;idOrPattern&gt;</c>: tree navigation
/// that delegates to SetCommand logic.
/// </summary>
public sealed class NavigationCommands(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    SetCommand setCommand,
    OutputFormatterFactory formatterFactory,
    ActiveItemResolver activeItemResolver,
    RenderingPipelineFactory? pipelineFactory = null)
{
    /// <summary>Navigate to the parent work item.</summary>
    // UpAsync: no disambiguation path — single parent, use formatter directly
    public async Task<int> UpAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Resolve active item with auto-fetch on cache miss
        var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value, ct);
        if (!resolveResult.TryGetWorkItem(out var item, out _, out _))
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        var parentChain = item!.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);
        var tree = WorkTree.Build(item, parentChain, children);

        var parentId = tree.MoveUp();
        if (parentId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("Already at root — no parent to navigate to."));
            return 1;
        }

        return await setCommand.ExecuteAsync(parentId.Value.ToString(), outputFormat, ct);
    }

    /// <summary>Navigate to a child work item by ID or pattern.</summary>
    public async Task<int> DownAsync(string? idOrPattern = null, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Resolve active item with auto-fetch on cache miss
        var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value, ct);
        if (!resolveResult.TryGetWorkItem(out var item, out _, out _))
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        var parentChain = item!.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);
        var tree = WorkTree.Build(item, parentChain, children);

        // No argument: present all children interactively(or auto-navigate if only one)
        if (string.IsNullOrEmpty(idOrPattern))
        {
            var candidates = children.Select(c => (c.Id, c.Title)).ToList();
            if (candidates.Count == 0)
            {
                Console.Error.WriteLine(fmt.FormatError("No children to navigate to."));
                return 1;
            }
            if (candidates.Count == 1)
                return await setCommand.ExecuteAsync(candidates[0].Id.ToString(), outputFormat, ct);

            if (renderer is not null)
            {
                var selected = await renderer.PromptDisambiguationAsync(candidates, ct);
                if (selected is not null)
                    return await setCommand.ExecuteAsync(selected.Value.Id.ToString(), outputFormat, ct);
                return 1;
            }
            Console.Error.WriteLine(fmt.FormatDisambiguation(candidates));
            return 1;
        }

        // DD-012: Use FindByPattern directly to preserve candidate list for disambiguation
        var matchResult = tree.FindByPattern(idOrPattern);

        switch (matchResult)
        {
            case MatchResult.SingleMatch single:
                return await setCommand.ExecuteAsync(single.Id.ToString(), outputFormat, ct);

            case MatchResult.MultipleMatches multi:
                if (renderer is not null)
                {
                    var selected = await renderer.PromptDisambiguationAsync(multi.Candidates, ct);
                    if (selected is not null)
                        return await setCommand.ExecuteAsync(selected.Value.Id.ToString(), outputFormat, ct);
                    return 1;
                }
                Console.Error.WriteLine(fmt.FormatDisambiguation(multi.Candidates));
                return 1;

            case MatchResult.NoMatch:
                Console.Error.WriteLine(fmt.FormatError($"No child matches '{idOrPattern}'."));
                return 1;

            default:
                Console.Error.WriteLine(fmt.FormatError("Unexpected match result."));
                return 1;
        }
    }
}
