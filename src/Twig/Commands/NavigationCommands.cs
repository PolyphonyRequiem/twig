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
    // Optional — null for backward compat with tests that predate EPIC-005
    RenderingPipelineFactory? pipelineFactory = null)
{
    /// <summary>Navigate to the parent work item.</summary>
    // UpAsync: no disambiguation path — single parent, use formatter directly
    public async Task<int> UpAsync(string outputFormat = "human", CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        var item = await workItemRepo.GetByIdAsync(activeId.Value);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        var parentChain = item.ParentId.HasValue
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
    public async Task<int> DownAsync(string idOrPattern, string outputFormat = "human", CancellationToken ct = default)
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

        var item = await workItemRepo.GetByIdAsync(activeId.Value);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache."));
            return 1;
        }

        var parentChain = item.ParentId.HasValue
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(item.Id);
        var tree = WorkTree.Build(item, parentChain, children);

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
