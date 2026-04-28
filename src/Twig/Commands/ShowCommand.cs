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
/// Implements <c>twig show [id]</c>: read-only work item display.
/// When called with an ID, performs a cache-first lookup.
/// When called without an ID, resolves the active work item from context.
/// If no active item is set, emits a branch detection hint and exits 1.
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
    IProcessConfigurationProvider? processConfigProvider = null,
    IContextStore? contextStore = null,
    ActiveItemResolver? activeItemResolver = null,
    IPendingChangeStore? pendingChangeStore = null,
    WorkingSetService? workingSetService = null,
    TwigPaths? twigPaths = null,
    IAdoGitService? adoGitService = null)
{
    private readonly IContextStore? _contextStore = contextStore;
    private readonly ActiveItemResolver? _activeItemResolver = activeItemResolver;
    private readonly IPendingChangeStore? _pendingChangeStore = pendingChangeStore;
    private readonly WorkingSetService? _workingSetService = workingSetService;

    public async Task<int> ExecuteAsync(int? id = null, string outputFormat = OutputFormatterFactory.DefaultFormat, bool noRefresh = false, CancellationToken ct = default)
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

    private async Task<int> ExecuteCoreAsync(int? id, string outputFormat, bool noRefresh, CancellationToken ct)
    {
        var (fmt, renderer) = ctx.Resolve(outputFormat);

        Domain.Aggregates.WorkItem item;
        int resolvedId;

        if (id.HasValue)
        {
            // ── By-ID path — cache-first lookup ──
            resolvedId = id.Value;
            var cached = await workItemRepo.GetByIdAsync(resolvedId, ct);
            if (cached is null)
            {
                ctx.StderrWriter.WriteLine($"error: Work item #{resolvedId} not found in local cache. Run 'twig set {resolvedId}' to fetch it.");
                return 1;
            }
            item = cached;
        }
        else
        {
            // ── No-args path — resolve from active context ──
            if (_contextStore is null || _activeItemResolver is null)
            {
                ctx.StderrWriter.WriteLine("error: No work item ID specified and context services not available.");
                return 1;
            }

            var result = await _activeItemResolver.GetActiveItemAsync(ct);
            switch (result)
            {
                case ActiveItemResult.Found found:
                    item = found.WorkItem;
                    break;
                case ActiveItemResult.FetchedFromAdo fetched:
                    item = fetched.WorkItem;
                    break;
                case ActiveItemResult.Unreachable unreachable:
                    ctx.StderrWriter.WriteLine($"error: Active work item #{unreachable.Id} is not reachable: {unreachable.Reason}");
                    return 1;
                case ActiveItemResult.NoContext:
                default:
                    EmitBranchDetectionHint();
                    return 1;
            }
            resolvedId = item.Id;
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

        var gitContext = await BuildGitContextAsync(ct);

        Func<Task<IReadOnlyList<PendingChangeRecord>>> getPendingChanges = _pendingChangeStore is not null
            ? () => _pendingChangeStore.GetChangesAsync(item.Id)
            : () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>([]);

        // Non-TTY machine output: sync synchronously before emitting so consumers get fresh data.
        // The TTY path handles sync via RenderWithSyncAsync (two-pass: cached → sync → revised).
        if (renderer is null && !noRefresh)
        {
            try
            {
                await syncCoordinatorFactory.ReadOnly.SyncItemSetAsync([resolvedId]);

                // Reload data from cache after sync
                var freshItem = await workItemRepo.GetByIdAsync(resolvedId, ct);
                if (freshItem is not null)
                {
                    item = freshItem;
                    children = await workItemRepo.GetChildrenAsync(item.Id, ct);
                    parent = item.ParentId.HasValue
                        ? await workItemRepo.GetByIdAsync(item.ParentId.Value, ct)
                        : null;

                    try { links = await linkRepo.GetLinksAsync(item.Id, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                    childProgress = processConfigProvider.ComputeChildProgress(children);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sync failure is non-fatal — emit cache-only data
            }
        }

        if (renderer is not null)
        {
            Task RenderStaticAsync() => renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: getPendingChanges,
                ct: CancellationToken.None,
                fieldDefinitions: fieldDefs,
                statusFieldEntries: statusFieldEntries,
                childProgress: childProgress,
                links: links,
                parent: parent,
                children: children,
                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                gitContext: gitContext);

            if (renderer is SpectreRenderer spectreRenderer && !noRefresh)
            {
                Task<IRenderable> BuildView(Domain.Aggregates.WorkItem wi, Domain.Aggregates.WorkItem? pa, IReadOnlyList<Domain.Aggregates.WorkItem> ch, (int Done, int Total)? progress)
                    => spectreRenderer.BuildStatusViewAsync(wi,
                        getPendingChanges: getPendingChanges,
                        fieldDefinitions: fieldDefs,
                        statusFieldEntries: statusFieldEntries,
                        childProgress: progress,
                        links: links,
                        parent: pa,
                        children: ch,
                        cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                        gitContext: gitContext);

                try
                {
                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => BuildView(item, parent, children, childProgress),
                        performSync: () => syncCoordinatorFactory.ReadOnly.SyncItemSetAsync([resolvedId]),
                        buildRevisedView: async _ =>
                        {
                            var freshItem = await workItemRepo.GetByIdAsync(resolvedId, CancellationToken.None);
                            if (freshItem is null) return null;

                            var freshChildren = await workItemRepo.GetChildrenAsync(freshItem.Id, CancellationToken.None);
                            var freshParent = freshItem.ParentId.HasValue
                                ? await workItemRepo.GetByIdAsync(freshItem.ParentId.Value, CancellationToken.None)
                                : null;

                            IReadOnlyList<WorkItemLink> freshLinks = [];
                            try { freshLinks = await linkRepo.GetLinksAsync(freshItem.Id, CancellationToken.None); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

                            return await spectreRenderer.BuildStatusViewAsync(freshItem,
                                getPendingChanges: getPendingChanges,
                                fieldDefinitions: fieldDefs,
                                statusFieldEntries: statusFieldEntries,
                                childProgress: processConfigProvider.ComputeChildProgress(freshChildren),
                                links: freshLinks,
                                parent: freshParent,
                                children: freshChildren,
                                cacheStaleMinutes: ctx.Config.Display.CacheStaleMinutes,
                                gitContext: gitContext);
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
            (int FieldCount, int NoteCount)? pendingCounts = null;
            if (_pendingChangeStore is not null)
            {
                var pending = await _pendingChangeStore.GetChangesAsync(item.Id);
                if (pending.Count > 0)
                {
                    var noteCount = 0;
                    var fieldCount = 0;
                    foreach (var change in pending)
                    {
                        if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                            noteCount++;
                        else
                            fieldCount++;
                    }
                    pendingCounts = (fieldCount, noteCount);
                }
            }
            Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries, childProgress, pendingCounts, links, parent, children, gitContext: gitContext));
        }
        else if (fmt is JsonOutputFormatter jsonFmt)
        {
            (int FieldCount, int NoteCount)? pendingCounts = null;
            if (_pendingChangeStore is not null)
            {
                var pending = await _pendingChangeStore.GetChangesAsync(item.Id);
                if (pending.Count > 0)
                {
                    var noteCount = 0;
                    var fieldCount = 0;
                    foreach (var change in pending)
                    {
                        if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                            noteCount++;
                        else
                            fieldCount++;
                    }
                    pendingCounts = (fieldCount, noteCount);
                }
            }
            Console.WriteLine(jsonFmt.FormatWorkItem(item, showDirty: false, links, parent, children, gitContext: gitContext, pendingChanges: pendingCounts));
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

    /// <summary>
    /// Best-effort git context: detect current branch via filesystem, then look up linked PRs.
    /// Never throws — returns <see cref="GitContext.Empty"/> on any failure.
    /// </summary>
    private async Task<GitContext> BuildGitContextAsync(CancellationToken ct)
    {
        string? branch = null;
        if (twigPaths is not null)
        {
            var repoRoot = Path.GetDirectoryName(twigPaths.TwigDir);
            if (repoRoot is not null)
                branch = GitBranchReader.GetCurrentBranch(repoRoot);
        }

        if (branch is null)
            return GitContext.Empty;

        IReadOnlyList<PullRequestInfo> prs = [];
        if (adoGitService is not null)
        {
            try
            {
                prs = await adoGitService.GetPullRequestsForBranchAsync(branch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort — PR lookup failures are non-fatal
            }
        }

        return new GitContext(branch, prs);
    }

    /// <summary>
    /// Emits an error message when no active work item is set, with a hint derived from
    /// the current git branch name if it encodes a work item ID.
    /// </summary>
    private void EmitBranchDetectionHint()
    {
        ctx.StderrWriter.WriteLine("error: No active work item. Use 'twig set <id>' to set one.");

        if (twigPaths is null)
            return;

        var repoRoot = Path.GetDirectoryName(twigPaths.TwigDir);
        if (repoRoot is null)
            return;

        var branch = GitBranchReader.GetCurrentBranch(repoRoot);
        if (branch is null)
            return;

        var detectedId = ExtractWorkItemIdFromBranch(branch);
        if (detectedId.HasValue)
        {
            ctx.StderrWriter.WriteLine($"hint: Branch '{branch}' may reference work item #{detectedId.Value}.");
            ctx.StderrWriter.WriteLine($"      Try: twig set {detectedId.Value}");
        }
    }

    /// <summary>
    /// Extracts a work item ID from a branch name by scanning path segments for leading digits.
    /// Handles common conventions: <c>feature/1234-description</c>, <c>users/name/1234</c>,
    /// <c>bug/1234</c>, etc.
    /// </summary>
    internal static int? ExtractWorkItemIdFromBranch(string branchName)
    {
        foreach (var segment in branchName.Split('/'))
        {
            var dashIndex = segment.IndexOf('-');
            var candidate = dashIndex > 0 ? segment[..dashIndex] : segment;
            if (int.TryParse(candidate, out var id) && id > 0)
                return id;
        }

        return null;
    }
}
