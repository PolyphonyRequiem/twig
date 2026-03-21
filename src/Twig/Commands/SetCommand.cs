using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig set &lt;idOrPattern&gt;</c>: resolves a work item by ID or title pattern,
/// fetches from ADO if not cached, loads parent chain and children, and sets active context.
/// On cache miss (FetchedFromAdo), computes the working set and evicts non-working-set items.
/// On cache hit (Found), computes the working set but skips eviction (FR-012).
/// Both paths sync the full working set via <see cref="SyncCoordinator.SyncWorkingSetAsync"/>.
/// </summary>
public sealed class SetCommand(
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    SyncCoordinator syncCoordinator,
    WorkingSetService workingSetService,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    // Optional — null for backward compat with tests that predate EPIC-005
    RenderingPipelineFactory? pipelineFactory = null,
    IPromptStateWriter? promptStateWriter = null)
{
    public async Task<int> ExecuteAsync(string idOrPattern, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
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
        var fetchedFromAdo = false;

        if (int.TryParse(idOrPattern, out var id))
        {
            // Numeric ID: cache lookup then auto-fetch via ActiveItemResolver
            var result = await activeItemResolver.ResolveByIdAsync(id, ct);
            if (!result.TryGetWorkItem(out item, out var errId, out var errReason))
            {
                Console.Error.WriteLine(fmt.FormatError(errId is not null
                    ? $"Work item #{errId} could not be fetched: {errReason}"
                    : $"Work item #{id} not found."));
                return 1;
            }
            if (result is ActiveItemResult.FetchedFromAdo)
            {
                Console.WriteLine(fmt.FormatInfo($"Fetching work item {id} from ADO..."));
                fetchedFromAdo = true;
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
            var parentChain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            if (parentChain.Count == 0)
            {
                // Parent not in cache — auto-fetch via resolver (best-effort)
                await activeItemResolver.ResolveByIdAsync(item.ParentId.Value, ct);
            }
        }

        // Set as active context
        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        // Working set compute + evict + sync — best-effort, never fails the command
        try
        {
            // Compute working set (DD-06: pass item.IterationPath to avoid redundant ADO call)
            var workingSet = await workingSetService.ComputeAsync(item.IterationPath, ct);

            // Evict on cache miss only (FR-012: cache hit skips eviction)
            if (fetchedFromAdo)
            {
                await workItemRepo.EvictExceptAsync(workingSet.AllIds, ct);
            }

            // Sync working set (DD-07: replaces SyncChildrenAsync — superset of parents, children, sprint items)
            if (renderer is not null)
            {
                // TTY path: render work item inside buildCachedView (DD-10 / FR-015 fix)
                await renderer.RenderWithSyncAsync(
                    buildCachedView: () =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable>(
                            new Spectre.Console.Text(fmt.FormatWorkItem(item, showDirty: false))),
                    performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet, ct),
                    buildRevisedView: (syncResult) =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                    ct);
            }
            else
            {
                // Non-TTY path: print item then sync silently
                Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
                await syncCoordinator.SyncWorkingSetAsync(workingSet, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Working set sync is best-effort — print the item even if sync fails
            if (renderer is null)
                Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
            Console.Error.WriteLine($"warning: working set sync failed: {ex.Message}");
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
