using System.Diagnostics;
using Spectre.Console.Rendering;
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
/// Implements <c>twig show &lt;id&gt;</c>: read-only work item lookup by integer ID.
/// Unlike <see cref="SetCommand"/>, this command does not change active context or record
/// navigation history. By default, renders cached data immediately then syncs the item
/// and revises the display. Use <c>--no-refresh</c> to skip the sync pass.
/// </summary>
public sealed class ShowCommand(
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    OutputFormatterFactory formatterFactory,
    SyncCoordinator syncCoordinator,
    TwigConfiguration config,
    RenderingPipelineFactory? pipelineFactory = null,
    TwigPaths? paths = null,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    IProcessConfigurationProvider? processConfigProvider = null,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public async Task<int> ExecuteAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, bool noRefresh = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(id, outputFormat, noRefresh, ct);
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

    private async Task<int> ExecuteCoreAsync(int id, string outputFormat, bool noRefresh, CancellationToken ct)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat)
            : (formatterFactory.GetFormatter(outputFormat), null);

        // Cache-first lookup — item must be in local cache
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
            Task RenderStaticAsync() => renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
                ct: CancellationToken.None,
                fieldDefinitions: fieldDefs,
                statusFieldEntries: statusFieldEntries,
                childProgress: childProgress,
                links: links,
                parent: parent,
                children: children,
                cacheStaleMinutes: config.Display.CacheStaleMinutes);

            if (renderer is SpectreRenderer spectreRenderer && !noRefresh)
            {
                Task<IRenderable> BuildView(Domain.Aggregates.WorkItem wi, Domain.Aggregates.WorkItem? pa, IReadOnlyList<Domain.Aggregates.WorkItem> ch, (int Done, int Total)? progress)
                    => spectreRenderer.BuildStatusViewAsync(wi,
                        getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
                        fieldDefinitions: fieldDefs,
                        statusFieldEntries: statusFieldEntries,
                        childProgress: progress,
                        links: links,
                        parent: pa,
                        children: ch,
                        cacheStaleMinutes: config.Display.CacheStaleMinutes);

                try
                {
                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => BuildView(item, parent, children, childProgress),
                        performSync: () => syncCoordinator.SyncItemSetAsync([id]),
                        buildRevisedView: async _ =>
                        {
                            var freshItem = await workItemRepo.GetByIdAsync(id, CancellationToken.None);
                            if (freshItem is null) return null;

                            var freshChildren = await workItemRepo.GetChildrenAsync(freshItem.Id, CancellationToken.None);
                            var freshParent = freshItem.ParentId.HasValue
                                ? await workItemRepo.GetByIdAsync(freshItem.ParentId.Value, CancellationToken.None)
                                : null;

                            IReadOnlyList<WorkItemLink> freshLinks = [];
                            try { freshLinks = await linkRepo.GetLinksAsync(freshItem.Id, CancellationToken.None); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                            return await spectreRenderer.BuildStatusViewAsync(freshItem,
                                getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]),
                                fieldDefinitions: fieldDefs,
                                statusFieldEntries: statusFieldEntries,
                                childProgress: processConfigProvider.ComputeChildProgress(freshChildren),
                                links: freshLinks,
                                parent: freshParent,
                                children: freshChildren,
                                cacheStaleMinutes: config.Display.CacheStaleMinutes);
                        },
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    await RenderStaticAsync();
                }
            }
            else
            {
                await RenderStaticAsync();
            }
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
