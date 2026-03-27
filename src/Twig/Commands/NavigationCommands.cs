using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig up</c>, <c>twig down</c>, <c>twig next</c>, and <c>twig prev</c>:
/// tree navigation that delegates to SetCommand logic.
/// Also implements <c>twig nav</c> (bare): interactive tree navigator.
/// </summary>
public sealed class NavigationCommands(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    ISeedLinkRepository seedLinkRepo,
    IWorkItemLinkRepository workItemLinkRepo,
    SetCommand setCommand,
    OutputFormatterFactory formatterFactory,
    ActiveItemResolver activeItemResolver,
    RenderingPipelineFactory? pipelineFactory = null,
    INavigationHistoryStore? historyStore = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Launch the interactive tree navigator.</summary>
    public async Task<int> InteractiveAsync(CancellationToken ct = default)
    {
        // Resolve renderer via pipeline factory (null when non-TTY / output redirected)
        IAsyncRenderer? renderer = null;
        if (pipelineFactory is not null)
        {
            var (_, r) = pipelineFactory.Resolve("human");
            renderer = r;
        }

        if (renderer is null)
        {
            Console.Error.WriteLine("Interactive navigation requires a terminal. Use: twig nav up, twig nav down, twig nav next, twig nav prev");
            return 0;
        }

        // Load initial state from active context
        var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
        if (activeId is null)
        {
            Console.Error.WriteLine("No active context. Use 'twig set <id>' to select a work item first.");
            return 0;
        }

        var initialItem = await workItemRepo.GetByIdAsync(activeId.Value, ct);
        if (initialItem is null)
        {
            Console.Error.WriteLine($"Work item #{activeId.Value} not found in cache. Run 'twig refresh' to fetch.");
            return 1;
        }

        var initialState = await LoadNodeStateAsync(activeId.Value, ct, initialItem);
        var result = await renderer.RenderInteractiveTreeAsync(initialState, id => LoadNodeStateAsync(id, ct), ct);

        if (result is not null)
        {
            await contextStore.SetActiveWorkItemIdAsync(result.Value, ct);
            if (historyStore is not null)
                await historyStore.RecordVisitAsync(result.Value, ct);
            if (promptStateWriter is not null)
                await promptStateWriter.WritePromptStateAsync();
        }

        return 0;
    }

    private async Task<TreeNavigatorState> LoadNodeStateAsync(int id, CancellationToken ct, Domain.Aggregates.WorkItem? preloadedItem = null)
    {
        var item = preloadedItem ?? await workItemRepo.GetByIdAsync(id, ct);

        var parentChain = item?.ParentId.HasValue == true
            ? await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct)
            : Array.Empty<Domain.Aggregates.WorkItem>();

        var children = await workItemRepo.GetChildrenAsync(id, ct);

        // Siblings are children of the item's parent, or all root items for top-level items
        IReadOnlyList<Domain.Aggregates.WorkItem> siblings;
        if (item?.ParentId.HasValue == true)
        {
            siblings = await workItemRepo.GetChildrenAsync(item.ParentId.Value, ct);
        }
        else
        {
            siblings = await workItemRepo.GetRootItemsAsync(ct);
            if (siblings.Count == 0 && item is not null)
                siblings = new[] { item };
        }

        var links = await workItemLinkRepo.GetLinksAsync(id, ct);
        var seedLinks = await seedLinkRepo.GetLinksForItemAsync(id, ct);

        return new TreeNavigatorState(item, parentChain, siblings, children, links, seedLinks);
    }
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
            var candidates = children.Select(c => (c.Id, $"{c.Title} [{c.State}]")).ToList();
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
                // Enrich candidates with state for display (domain returns raw titles for pattern matching)
                var displayCandidates = multi.Candidates
                    .Select(c =>
                    {
                        var child = children.FirstOrDefault(ch => ch.Id == c.Id);
                        return child is not null ? (c.Id, $"{c.Title} [{child.State}]") : c;
                    })
                    .ToList();
                if (renderer is not null)
                {
                    var selected = await renderer.PromptDisambiguationAsync(displayCandidates, ct);
                    if (selected is not null)
                        return await setCommand.ExecuteAsync(selected.Value.Id.ToString(), outputFormat, ct);
                    return 1;
                }
                Console.Error.WriteLine(fmt.FormatDisambiguation(displayCandidates));
                return 1;

            case MatchResult.NoMatch:
                Console.Error.WriteLine(fmt.FormatError($"No child matches '{idOrPattern}'."));
                return 1;

            default:
                Console.Error.WriteLine(fmt.FormatError("Unexpected match result."));
                return 1;
        }
    }

    /// <summary>Navigate to the next sibling work item (by successor links, then fallback to display order).</summary>
    public async Task<int> NextAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await NavigateSiblingAsync(direction: +1, outputFormat, ct);

    /// <summary>Navigate to the previous sibling work item (by predecessor links, then fallback to display order).</summary>
    public async Task<int> PrevAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => await NavigateSiblingAsync(direction: -1, outputFormat, ct);

    private async Task<int> NavigateSiblingAsync(int direction, string outputFormat, CancellationToken ct)
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

        if (!item!.ParentId.HasValue)
        {
            Console.Error.WriteLine(fmt.FormatError("Cannot navigate siblings — item has no parent."));
            return 1;
        }

        // Try link-based navigation first (successor/predecessor)
        var linkTarget = await FindLinkedSiblingAsync(item, direction, ct);
        if (linkTarget.HasValue)
            return await setCommand.ExecuteAsync(linkTarget.Value.ToString(), outputFormat, ct);

        // Fallback: navigate by display order (same ordering as GetChildrenAsync)
        var siblings = await workItemRepo.GetChildrenAsync(item.ParentId.Value, ct);
        var ordered = siblings.ToList(); // already in display order from repo

        var currentIndex = ordered.FindIndex(s => s.Id == item.Id);
        if (currentIndex < 0)
        {
            Console.Error.WriteLine(fmt.FormatError("Current item not found among siblings."));
            return 1;
        }

        var targetIndex = currentIndex + direction;

        if (targetIndex < 0)
        {
            Console.Error.WriteLine(fmt.FormatError($"Already at first sibling under #{item.ParentId.Value}."));
            return 1;
        }

        if (targetIndex >= ordered.Count)
        {
            Console.Error.WriteLine(fmt.FormatError($"Already at last sibling under #{item.ParentId.Value}."));
            return 1;
        }

        return await setCommand.ExecuteAsync(ordered[targetIndex].Id.ToString(), outputFormat, ct);
    }

    /// <summary>
    /// Finds the next or previous sibling by following successor/predecessor links.
    /// Checks seed links (for unpublished seeds) and work item links (for published items).
    /// Returns null if no link-based sibling was found.
    /// </summary>
    private async Task<int?> FindLinkedSiblingAsync(Domain.Aggregates.WorkItem item, int direction, CancellationToken ct)
    {
        // Check seed links (covers unpublished seeds)
        var seedLinks = await seedLinkRepo.GetLinksForItemAsync(item.Id, ct);

        int? targetId;
        if (direction > 0) // next: follow successor link where we are the source
        {
            targetId = seedLinks
                .Where(l => l.SourceId == item.Id && l.LinkType == SeedLinkTypes.Successor)
                .Select(l => (int?)l.TargetId)
                .FirstOrDefault();
        }
        else // prev: follow successor link where we are the target (reverse direction)
        {
            targetId = seedLinks
                .Where(l => l.TargetId == item.Id && l.LinkType == SeedLinkTypes.Successor)
                .Select(l => (int?)l.SourceId)
                .FirstOrDefault();
        }

        if (targetId.HasValue)
            return targetId;

        // Check work item links (covers published items)
        var workLinks = await workItemLinkRepo.GetLinksAsync(item.Id, ct);

        if (direction > 0)
        {
            targetId = workLinks
                .Where(l => l.LinkType == LinkTypes.Successor)
                .Select(l => (int?)l.TargetId)
                .FirstOrDefault();
        }
        else
        {
            targetId = workLinks
                .Where(l => l.LinkType == LinkTypes.Predecessor)
                .Select(l => (int?)l.TargetId)
                .FirstOrDefault();
        }

        return targetId;
    }
}
