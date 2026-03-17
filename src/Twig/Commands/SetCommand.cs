using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig set &lt;idOrPattern&gt;</c>: resolves a work item by ID or title pattern,
/// fetches from ADO if not cached, loads parent chain and children, and sets active context.
/// </summary>
public sealed class SetCommand(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IContextStore contextStore,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    // Optional — null for backward compat with tests that predate EPIC-005
    RenderingPipelineFactory? pipelineFactory = null,
    IPromptStateWriter? promptStateWriter = null)
{
    public async Task<int> ExecuteAsync(string idOrPattern, string outputFormat = "human", CancellationToken ct = default)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        if (string.IsNullOrWhiteSpace(idOrPattern))
        {
            Console.Error.WriteLine(fmt.FormatError("Usage: twig set <id or pattern>"));
            return 2;
        }

        Domain.Aggregates.WorkItem? item = null;

        if (int.TryParse(idOrPattern, out var id))
        {
            // Numeric ID: check cache first, then fetch from ADO
            item = await workItemRepo.GetByIdAsync(id);
            if (item is null)
            {
                Console.WriteLine(fmt.FormatInfo($"Fetching work item {id} from ADO..."));
                item = await adoService.FetchAsync(id);
                await workItemRepo.SaveAsync(item);
            }
        }
        else
        {
            // Pattern: search cache first
            var cached = await workItemRepo.FindByPatternAsync(idOrPattern);
            if (cached.Count == 1)
            {
                item = cached[0];
            }
            else if (cached.Count > 1)
            {
                var matches = cached.Select(c => (c.Id, c.Title)).ToList();

                if (renderer is not null)
                {
                    // Interactive disambiguation — TTY + human format
                    var selected = await renderer.PromptDisambiguationAsync(matches, ct);
                    if (selected is not null)
                    {
                        item = cached.FirstOrDefault(c => c.Id == selected.Value.Id);
                        if (item is null)
                            return 1;
                    }
                    else
                    {
                        return 1;
                    }
                }
                else
                {
                    // Static list fallback — JSON, minimal, piped, non-TTY
                    Console.Error.WriteLine(fmt.FormatDisambiguation(matches));
                    return 1;
                }
            }
            else
            {
                Console.Error.WriteLine(fmt.FormatError($"No cached items match '{idOrPattern}'."));
                return 1;
            }
        }

        // Fetch parent chain and children, cache them
        if (item.ParentId.HasValue)
        {
            var parentChain = await workItemRepo.GetParentChainAsync(item.ParentId.Value);
            if (parentChain.Count == 0)
            {
                try
                {
                    var parent = await adoService.FetchAsync(item.ParentId.Value);
                    await workItemRepo.SaveAsync(parent);
                }
                catch (HttpRequestException) { /* Parent may not be accessible */ }
            }
        }

        var children = await adoService.FetchChildrenAsync(item.Id);
        if (children.Count > 0)
            await workItemRepo.SaveBatchAsync(children);

        // Set as active context
        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        promptStateWriter?.WritePromptState();

        Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));

        var hints = hintEngine.GetHints("set",
            item: item,
            outputFormat: outputFormat);
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }
}
