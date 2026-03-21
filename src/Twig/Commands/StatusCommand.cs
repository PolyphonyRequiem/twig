using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
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
    RenderingPipelineFactory? pipelineFactory = null,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    public async Task<int> ExecuteAsync(string outputFormat = "human", bool noLive = false, CancellationToken ct = default)
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
                    Console.Error.WriteLine(formattedHint);
            }

            Console.Error.WriteLine(fmt.FormatError("No active work item. Run 'twig set <id>' first."));
            return 1;
        }

        // Use ActiveItemResolver for auto-fetch on cache miss (G-3)
        var resolveResult = await activeItemResolver.GetActiveItemAsync();
        if (!resolveResult.TryGetWorkItem(out var item, out var unreachableId, out var unreachableReason))
        {
            var errorMsg = unreachableId is not null
                ? $"Work item #{unreachableId} not found in cache and could not be fetched. Run 'twig set {unreachableId}' to refresh."
                : $"Work item #{activeId.Value} not found in cache. Run 'twig set {activeId.Value}' to refresh.";
            Console.Error.WriteLine(fmt.FormatError(errorMsg));
            return 1;
        }

        if (renderer is not null)
        {
            // Async progressive rendering path — dashboard layout via SpectreRenderer
            await renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: () => pendingChangeStore.GetChangesAsync(item.Id),
                ct: CancellationToken.None);

            // Sync working set after cached render (EPIC-004) — best-effort
            try
            {
                var workingSet = await workingSetService.ComputeAsync(item.IterationPath);
                await renderer.RenderWithSyncAsync(
                    buildCachedView: () =>
                        Task.FromResult<Spectre.Console.Rendering.IRenderable>(
                            new Spectre.Console.Text(string.Empty)),
                    performSync: () => syncCoordinator.SyncWorkingSetAsync(workingSet),
                    buildRevisedView: syncResult => syncResult is SyncResult.Updated
                        ? Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null)
                        : Task.FromResult<Spectre.Console.Rendering.IRenderable?>(null),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* sync is best-effort — don't fail the command */ }

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
        Console.WriteLine(fmt.FormatWorkItem(item, showDirty: true));

        // Git context enrichment (EPIC-006) — additive, never changes existing behavior
        await WriteGitContextAsync(fmt);

        var pending = await pendingChangeStore.GetChangesAsync(item.Id);
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

            Console.WriteLine(fmt.FormatInfo($"  Pending:   {fieldCount} field change(s), {noteCount} note(s)"));
        }

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
}
