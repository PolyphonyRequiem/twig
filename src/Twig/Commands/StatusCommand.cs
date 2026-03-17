using System.Globalization;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig status</c>: displays the active work item with pending change counts.
/// </summary>
public sealed class StatusCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IPendingChangeStore pendingChangeStore,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    // Optional — null for backward compat with tests that predate EPIC-004
    RenderingPipelineFactory? pipelineFactory = null,
    IGitService? gitService = null,
    IAdoGitService? adoGitService = null)
{
    public async Task<int> ExecuteAsync(string outputFormat = "human", bool noLive = false)
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

        var item = await workItemRepo.GetByIdAsync(activeId.Value);
        if (item is null)
        {
            Console.Error.WriteLine(fmt.FormatError($"Work item #{activeId.Value} not found in cache. Run 'twig set {activeId.Value}' to refresh."));
            return 1;
        }

        if (renderer is not null)
        {
            // Async progressive rendering path — dashboard layout via SpectreRenderer
            await renderer.RenderStatusAsync(
                getItem: () => Task.FromResult<Domain.Aggregates.WorkItem?>(item),
                getPendingChanges: () => pendingChangeStore.GetChangesAsync(item.Id),
                ct: CancellationToken.None);

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

        return 0;
    }

    /// <summary>
    /// Writes branch name and linked PR info to stdout.
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
