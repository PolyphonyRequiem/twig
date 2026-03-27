using System.Diagnostics;
using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig status</c>: displays the active work item with pending change counts.
/// After rendering cached status, syncs the working set and revises the display if data changed.
/// </summary>
public sealed class StatusCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    SyncCoordinator syncCoordinator,
    TwigPaths paths,
    RenderingPipelineFactory? pipelineFactory = null,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null,
    IFieldDefinitionStore? fieldDefinitionStore = null,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool noLive = false, CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, noLive, ct);
        telemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
        {
            ["command"] = "status",
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

    private async Task<int> ExecuteCoreAsync(string outputFormat, bool noLive, CancellationToken ct)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat, noLive)
            : (formatterFactory.GetFormatter(outputFormat), null);

        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            // Passive branch detection hint: suggest 'twig set' if branch matches a cached item
            var branchHint = await hintEngine.GetBranchDetectionHintAsync(
                activeContextId: null,
                gitService: gitService,
                workItemRepo: workItemRepo,
                branchPattern: config.Git.BranchPattern,
                outputFormat: outputFormat);
            if (branchHint is not null)
            {
                var formattedHint = fmt.FormatHint(branchHint);
                if (!string.IsNullOrEmpty(formattedHint))
                    _stderr.WriteLine(formattedHint);
            }

            _stderr.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Use ActiveItemResolver for auto-fetch on cache miss (G-3)
        var resolveResult = await activeItemResolver.GetActiveItemAsync();
        if (!resolveResult.TryGetWorkItem(out var item, out var unreachableId, out var unreachableReason))
        {
            var errorMsg = unreachableId is not null
                ? $"Work item #{unreachableId} not found in cache and could not be fetched. Run 'twig set {unreachableId}' to refresh."
                : $"Work item #{activeId.Value} not found in cache. Run 'twig set {activeId.Value}' to refresh.";
            _stderr.WriteLine(fmt.FormatError(errorMsg));
            return 1;
        }

        // Load field definitions and status-fields config (best-effort, shared by both paths)
        var fieldDefs = fieldDefinitionStore is not null
            ? await fieldDefinitionStore.GetAllAsync(ct)
            : null;

        IReadOnlyList<StatusFieldEntry>? statusFieldEntries = null;
        if (File.Exists(paths.StatusFieldsPath))
        {
            try
            {
                var configContent = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
                statusFieldEntries = StatusFieldsConfig.Parse(configContent);
            }
            catch { /* best-effort — fall back to default behavior */ }
        }

        if (renderer is not null)
        {
            // EPIC-004 ITEM-019: Compute child progress for parent items
            var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
            var childProgress = ComputeChildProgress(children);

            // FIX-002: Render status panel INSIDE the Live region to prevent border
            // corruption when the sync indicator clears. BuildStatusViewAsync returns
            // the complete status view as a composite IRenderable.
            if (renderer is SpectreRenderer spectreRenderer)
            {
                try
                {
                    var workingSet = await workingSetService.ComputeAsync(item.IterationPath);
                    await renderer.RenderWithSyncAsync(
                        buildCachedView: () => spectreRenderer.BuildStatusViewAsync(
                            getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                            getPendingChanges: () => pendingChangeStore.GetChangesAsync(item.Id),
                            ct: CancellationToken.None,
                            fieldDefinitions: fieldDefs,
                            statusFieldEntries: statusFieldEntries,
                            childProgress: childProgress),
                        performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet),
                        buildRevisedView: syncResult => Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                        CancellationToken.None);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // Sync failed — fall back to static render
                    await renderer.RenderStatusAsync(
                        getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                        getPendingChanges: () => pendingChangeStore.GetChangesAsync(item.Id),
                        ct: CancellationToken.None,
                        fieldDefinitions: fieldDefs,
                        statusFieldEntries: statusFieldEntries,
                        childProgress: childProgress);
                }
            }
            else
            {
                // Non-SpectreRenderer path — render directly (no sync indicator)
                await renderer.RenderStatusAsync(
                    getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                    getPendingChanges: () => pendingChangeStore.GetChangesAsync(item.Id),
                    ct: CancellationToken.None,
                    fieldDefinitions: fieldDefs,
                    statusFieldEntries: statusFieldEntries,
                    childProgress: childProgress);
            }

            var seeds = await workItemRepo.GetSeedsAsync();
            var staleSeedCount = Workspace.Build(item, [], seeds)
                .GetStaleSeeds(config.Seed.StaleDays).Count;

            // Check cache freshness (EPIC-006) and include stale hint if needed
            var lastRefreshedRaw = await contextStore.GetValueAsync("last_refreshed_at");
            var staleHint = WorkspaceCommand.IsCacheStale(lastRefreshedRaw, config.Display.CacheStaleMinutes)
                ? "Data may be stale. Run 'twig refresh' to update."
                : null;

            var hints = hintEngine.GetHints("status",
                item: item,
                outputFormat: outputFormat,
                staleSeedCount: staleSeedCount);

            var allHints = staleHint is not null
                ? new List<string>(hints) { staleHint }
                : hints;
            renderer.RenderHints(allHints);

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, piped output)
        var summary = fmt.FormatStatusSummary(item);
        if (!string.IsNullOrEmpty(summary))
            Console.WriteLine(summary);

        // Use overload with field definitions when formatter supports it
        if (fmt is HumanOutputFormatter humanFmt)
        {
            // EPIC-004 ITEM-019: Compute child progress for sync path
            var syncChildren = await workItemRepo.GetChildrenAsync(item.Id, ct);
            var syncChildProgress = ComputeChildProgress(syncChildren);

            // EPIC-004 ITEM-019: Compute pending changes for consolidated footer
            var syncPending = await pendingChangeStore.GetChangesAsync(item.Id);
            (int FieldCount, int NoteCount)? syncPendingChanges = null;
            if (syncPending.Count > 0)
            {
                var syncNoteCount = 0;
                var syncFieldCount = 0;
                foreach (var change in syncPending)
                {
                    if (string.Equals(change.ChangeType, "note", StringComparison.OrdinalIgnoreCase))
                        syncNoteCount++;
                    else
                        syncFieldCount++;
                }
                syncPendingChanges = (syncFieldCount, syncNoteCount);
            }

            Console.WriteLine(humanFmt.FormatWorkItem(item, showDirty: true, fieldDefs, statusFieldEntries, syncChildProgress, syncPendingChanges));
        }
        else
            Console.WriteLine(fmt.FormatWorkItem(item, showDirty: true));

        // Git context enrichment (EPIC-006) — additive, never changes existing behavior
        await WriteGitContextAsync(fmt);

        var syncSeeds = await workItemRepo.GetSeedsAsync();
        var syncStaleSeedCount = Workspace.Build(item, [], syncSeeds)
            .GetStaleSeeds(config.Seed.StaleDays).Count;

        // Check cache freshness (EPIC-006) and include stale hint if needed
        var syncLastRefreshedRaw = await contextStore.GetValueAsync("last_refreshed_at");
        var syncStaleHint = WorkspaceCommand.IsCacheStale(syncLastRefreshedRaw, config.Display.CacheStaleMinutes)
            ? "Data may be stale. Run 'twig refresh' to update."
            : null;

        var syncHints = hintEngine.GetHints("status",
            item: item,
            outputFormat: outputFormat,
            staleSeedCount: syncStaleSeedCount);

        var allSyncHints = syncStaleHint is not null
            ? new List<string>(syncHints) { syncStaleHint }
            : (IReadOnlyList<string>)syncHints;
        foreach (var hint in allSyncHints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        // Sync working set silently after output (EPIC-004)
        var syncWorkingSet = await workingSetService.ComputeAsync(item.IterationPath);
        await syncCoordinator.SyncWorkingSetAsync(syncWorkingSet);

        return 0;
    }
    /// <summary>
    /// Gracefully degrades when git is unavailable or not in a repo.
    /// </summary>
    private async Task WriteGitContextAsync(IOutputFormatter fmt)
    {
        if (gitService is null) return;

        string? branchName = null;
        try
        {
            var isInWorkTree = await gitService.IsInsideWorkTreeAsync();
            if (!isInWorkTree) return;
            branchName = await gitService.GetCurrentBranchAsync();
        }
        catch
        {
            return; // Git operations are best-effort
        }

        if (branchName is not null)
        {
            Console.WriteLine(fmt.FormatBranchInfo(branchName));
        }

        // Linked PRs
        if (adoGitService is not null && branchName is not null)
        {
            try
            {
                var prs = await adoGitService.GetPullRequestsForBranchAsync(branchName);
                foreach (var pr in prs)
                    Console.WriteLine(fmt.FormatPrStatus(pr.PullRequestId, pr.Title, pr.Status));
            }
            catch
            {
                // PR lookup is best-effort
            }
        }
    }

    private static (int Done, int Total)? ComputeChildProgress(IReadOnlyList<Domain.Aggregates.WorkItem> children)
    {
        if (children.Count == 0)
            return null;

        var done = 0;
        foreach (var child in children)
        {
            var cat = Domain.Services.StateCategoryResolver.Resolve(child.State, null);
            if (cat == Domain.Enums.StateCategory.Resolved || cat == Domain.Enums.StateCategory.Completed)
                done++;
        }
        return (done, children.Count);
    }
}
