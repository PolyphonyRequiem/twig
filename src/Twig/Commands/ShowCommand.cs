using System.Diagnostics;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig show &lt;id&gt;</c>: read-only, cache-only work item lookup by integer ID.
/// Unlike <see cref="SetCommand"/>, this command does not change active context, trigger a sync,
/// or record navigation history. All data comes exclusively from the local cache.
/// </summary>
public sealed class ShowCommand(
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    OutputFormatterFactory formatterFactory,
    RenderingPipelineFactory? pipelineFactory = null,
    TwigPaths? paths = null,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    IProcessConfigurationProvider? processConfigProvider = null,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public async Task<int> ExecuteAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(id, outputFormat, ct);
        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "show",
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

    private async Task<int> ExecuteCoreAsync(int id, string outputFormat, CancellationToken ct)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        // Cache-only lookup — no ADO fetch, no sync
        var item = await workItemRepo.GetByIdAsync(id, ct);
        if (item is null)
        {
            _stderr.WriteLine($"error: Work item #{id} not found in local cache. Run 'twig set {id}' to fetch it.");
            return 1;
        }

        // Enrichment — all cache-only, best-effort
        var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
        Domain.Aggregates.WorkItem? parent = item.ParentId.HasValue
            ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
            : null;

        IReadOnlyList<WorkItemLink> links = [];
        try { links = await linkRepo.GetLinksAsync(item.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var fieldDefs = fieldDefinitionStore is not null
            ? await fieldDefinitionStore.GetAllAsync(ct)
            : null;

        IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null;
        if (paths is not null && File.Exists(paths.StatusFieldsPath))
        {
            try
            {
                var configContent = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
                statusFieldEntries = StatusFieldsConfig.Parse(configContent);
            }
            catch { /* best-effort */ }
        }

        var childProgress = processConfigProvider.ComputeChildProgress(children);

        if (renderer is not null)
        {
            // async renderer with empty pending changes (read-only view)
            await renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
                ct: ct,
                fieldDefinitions: fieldDefs,
                statusFieldEntries: statusFieldEntries,
                childProgress: childProgress,
                links: links,
                parent: parent,
                children: children);
        }
        else if (fmt is HumanOutputFormatter humanFmt)
        {
            Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries, childProgress, pendingChanges: null, links, parent, children));
        }
        else if (fmt is JsonOutputFormatter jsonFmt)
        {
            Console.WriteLine(jsonFmt.FormatWorkItem(item, showDirty: false, links, parent, children));
        }
        else
        {
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        }

        return 0;
    }
}
