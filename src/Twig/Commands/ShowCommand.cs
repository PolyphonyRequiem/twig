using System.Diagnostics;
using Spectre.Console.Rendering;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
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
    CommandContext ctx,
    IWorkItemRepository workItemRepo,
    IWorkItemLinkRepository linkRepo,
    SyncCoordinatorFactory syncCoordinatorFactory,
    StatusFieldConfigReader statusFieldReader,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    IProcessConfigurationProvider? processConfigProvider = null)
{
    public async Task<int> ExecuteAsync(int id, string outputFormat = OutputFormatterFactory.DefaultFormat, bool noRefresh = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(id, outputFormat, noRefresh, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    /// <summary>
    /// Batch lookup: accepts comma-separated IDs, returns all found items.
    /// Cache-only — no ADO fetch. Missing IDs are silently skipped.
    /// </summary>
    public async Task<int> ExecuteBatchAsync(string batch, string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteBatchCoreAsync(batch, outputFormat, ct);
        TelemetryHelper.TrackCommand(ctx.TelemetryClient, "show-batch", outputFormat, exitCode, startTimestamp);
        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(int id, string outputFormat, bool noRefresh, CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat);

        // Cache-first lookup — item must be in local cache
        var item = await workItemRepo.GetByIdAsync(id, ct);
        if (item is null)
        {
            ctx.StderrWriter.WriteLine($"error: Work item #{id} not found in local cache. Run 'twig set {id}' to fetch it.");
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

        var statusFieldEntries = await statusFieldReader.ReadAsync(ct);

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
                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes);

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
                        cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes);

                try
                {
                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => BuildView(item, parent, children, childProgress),
                        performSync: () => syncCoordinatorFactory.ReadOnly.SyncItemSetAsync([id]),
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
                                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes);
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

    private async Task<int> ExecuteBatchCoreAsync(string batch, string outputFormat, CancellationToken ct)
    {
        var ids = ParseBatchIds(batch);
        var items = new List<Domain.Aggregates.WorkItem>();

        foreach (var id in ids)
        {
            var item = await workItemRepo.GetByIdAsync(id, ct);
            if (item is not null)
                items.Add(item);
        }

        var fmt = ctx.FormatterFactory.GetFormatter(outputFormat);

        if (fmt is JsonOutputFormatter jsonFmt)
        {
            Console.WriteLine(jsonFmt.FormatWorkItemBatch(items));
        }
        else
        {
            foreach (var item in items)
                Console.WriteLine(fmt.FormatWorkItem(item, showDirty: false));
        }

        return 0;
    }

    private static List<int> ParseBatchIds(string batch)
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(batch))
            return ids;

        foreach (var segment in batch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(segment, out var id))
                ids.Add(id);
        }

        return ids;
    }
}
