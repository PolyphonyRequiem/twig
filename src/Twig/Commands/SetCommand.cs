using System.Diagnostics;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig set &lt;idOrPattern&gt;</c>: resolves a work item by ID or title pattern,
/// fetches from ADO if not cached, sets active context, and emits a single confirmation line.
/// </summary>
public sealed class SetCommand(
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    IPromptStateWriter? promptStateWriter = null,
    INavigationHistoryStore? historyStore = null)
{
    public async Task<int> ExecuteAsync(string idOrPattern, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(idOrPattern, outputFormat, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "set", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(string idOrPattern, string outputFormat, CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat);

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

        await contextStore.SetActiveWorkItemIdAsync(item.Id);
        if (historyStore is not null)
            await historyStore.RecordVisitAsync(item.Id, ct);
        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        Console.WriteLine(fmt.FormatSetConfirmation(item));

        return 0;
    }
}
