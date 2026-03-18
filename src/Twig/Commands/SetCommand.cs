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
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
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
            // Numeric ID: cache lookup then auto-fetch via ActiveItemResolver
            var result = await activeItemResolver.ResolveByIdAsync(id, ct);
            switch (result)
            {
                case ActiveItemResult.Found found:
                    item = found.WorkItem;
                    break;
                case ActiveItemResult.FetchedFromAdo fetched:
                    Console.WriteLine(fmt.FormatInfo($"Fetching work item {id} from ADO..."));
                    item = fetched.WorkItem;
                    break;
                case ActiveItemResult.Unreachable unreachable:
                    Console.Error.WriteLine(fmt.FormatError($"Work item #{unreachable.Id} could not be fetched: {unreachable.Reason}"));
                    return 1;
                default:
                    Console.Error.WriteLine(fmt.FormatError($"Work item #{id} not found."));
                    return 1;
            }
        }
        else
        {
            // Pattern: search cache first — DD-10: disambiguation remains inline
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

        // Hydrate parent chain via ActiveItemResolver (auto-fetch on miss)
        if (item.ParentId.HasValue)
        {
            var parentChain = await workItemRepo.GetParentChainAsync(item.ParentId.Value);
            if (parentChain.Count == 0)
            {
                // Parent not in cache — auto-fetch via resolver (best-effort)
                await activeItemResolver.ResolveByIdAsync(item.ParentId.Value, ct);
            }
        }

        // Set as active context
        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        promptStateWriter?.WritePromptState();

        Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));

        // Sync children via SyncCoordinator (DD-15: always fetches unconditionally)
        if (renderer is not null)
        {
            // TTY path: show sync status below the work item via Live() context
            await renderer.RenderWithSyncAsync(
                buildCachedView: () =>
                    Task.FromResult<Spectre.Console.Rendering.IRenderable>(new Spectre.Console.Text("")),
                performSync: () => syncCoordinator.SyncChildrenAsync(item.Id, ct),
                buildRevisedView: (syncResult) =>
                    Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                ct);
        }
        else
        {
            // Non-TTY path: sync silently
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
            await syncCoordinator.SyncChildrenAsync(item.Id, ct);
        }

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
