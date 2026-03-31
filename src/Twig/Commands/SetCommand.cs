using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;
using Twig.Hints;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig set &lt;idOrPattern&gt;</c>: resolves a work item by ID or title pattern,
/// fetches from ADO if not cached, loads parent chain, and sets active context.
/// On cache miss (FetchedFromAdo), computes the working set for eviction (FR-012),
/// then syncs only the target item and parent chain via <see cref="SyncCoordinator.SyncItemSetAsync"/>.
/// On cache hit (Found), skips working set computation and syncs only the target + parents.
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
    IPromptStateWriter? promptStateWriter = null,
    INavigationHistoryStore? historyStore = null,
    ITelemetryClient? telemetryClient = null)
{
    public async Task<int> ExecuteAsync(string idOrPattern, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(idOrPattern, outputFormat, ct);
        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "set",
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

    private async Task<int> ExecuteCoreAsync(string idOrPattern, string outputFormat, CancellationToken ct)
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
                var matches = cached.Select(c => (c.Id, $"{c.Title} [{c.State}]")).ToList();

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
        // Capture parent chain IDs for targeted sync (DD-1: sync scope = target + parents)
        var parentChainIds = new List<int>();
        if (item.ParentId.HasValue)
        {
            var parentChain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            if (parentChain.Count == 0)
            {
                // Parent not in cache — auto-fetch via resolver (best-effort)
                await activeItemResolver.ResolveByIdAsync(item.ParentId.Value, ct);
                parentChain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            }
            parentChainIds.AddRange(parentChain.Select(p => p.Id));
        }

        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        if (historyStore is not null)
            await historyStore.RecordVisitAsync(item.Id, ct);
        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        // Print item immediately (DD-5: unified TTY and non-TTY output)
        Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));

        // Targeted sync — best-effort, never fails the command (DD-1, DD-2)
        try
        {
            // Cache-miss path: compute working set for eviction (FR-012 unchanged)
            if (fetchedFromAdo)
            {
                var workingSet = await workingSetService.ComputeAsync(item.IterationPath, ct);
                await workItemRepo.EvictExceptAsync(workingSet.AllIds, ct);
            }

            // Sync target item + parent chain only (DD-2: SyncItemSetAsync skips full working set)
            await syncCoordinator.SyncItemSetAsync([item.Id, ..parentChainIds], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: sync failed: {ex.Message}");
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
