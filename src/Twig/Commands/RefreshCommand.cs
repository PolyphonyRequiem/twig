using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig refresh</c>: fetches the workspace scope from ADO and updates the local cache.
/// Seeds (local-only items) are skipped. Protected items (dirty/pending) are guarded against overwrite.
/// </summary>
public sealed class RefreshCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IIterationService iterationService,
    IPendingChangeStore pendingChangeStore,
    ProtectedCacheWriter protectedCacheWriter,
    TwigConfiguration config,
    TwigPaths paths,
    IProcessTypeStore processTypeStore,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    WorkingSetService workingSetService,
    SyncCoordinator syncCoordinator,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>Refresh the local cache from Azure DevOps.</summary>
    /// <param name="outputFormat">Output format: human, json, or minimal.</param>
    /// <param name="force">When true, bypass the dirty guard and overwrite protected items.</param>
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool force = false, CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        _ = hintEngine; // No registered hints for refresh

        Console.WriteLine(fmt.FormatInfo("Refreshing from ADO..."));

        // Get current iteration
        var iteration = await iterationService.GetCurrentIterationAsync();
        Console.WriteLine(fmt.FormatInfo($"  Iteration: {iteration}"));

        // Query all items in the current sprint, scoped to team area paths if configured
        // Escape single quotes to prevent WIQL injection from unusual iteration/area path values.
        var sanitizedPath = iteration.Value.Replace("'", "''");
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.IterationPath] = '{sanitizedPath}'";

        // Build area path filter: prefer AreaPathEntries (with IncludeChildren), fall back to AreaPaths/AreaPath
        var areaPathEntries = config.Defaults?.AreaPathEntries;
        if (areaPathEntries is { Count: > 0 })
        {
            var clauses = areaPathEntries
                .Select(entry =>
                {
                    var escaped = entry.Path.Replace("'", "''");
                    var op = entry.IncludeChildren ? "UNDER" : "=";
                    return $"[System.AreaPath] {op} '{escaped}'";
                });
            wiql += $" AND ({string.Join(" OR ", clauses)})";
        }
        else
        {
            var areaPaths = config.Defaults?.AreaPaths;
            if (areaPaths is null || areaPaths.Count == 0)
            {
                var singlePath = config.Defaults?.AreaPath;
                if (!string.IsNullOrWhiteSpace(singlePath))
                    areaPaths = [singlePath];
            }

            if (areaPaths is { Count: > 0 })
            {
                var clauses = areaPaths
                    .Select(ap => $"[System.AreaPath] UNDER '{ap.Replace("'", "''")}'");
                wiql += $" AND ({string.Join(" OR ", clauses)})";
            }
        }

        wiql += " ORDER BY [System.Id]";
        var ids = await adoService.QueryByWiqlAsync(wiql);

        if (ids.Count == 0)
        {
            Console.WriteLine(fmt.FormatInfo("  No items found in current iteration."));
        }
        else
        {
            // Fetch items in batch (skip seeds which have negative IDs)
            var realIds = ids.Where(id => id > 0).ToList();

            // Compute protected IDs for informational conflict detection
            var protectedIds = !force
                ? await SyncGuard.GetProtectedItemIdsAsync(workItemRepo, pendingChangeStore)
                : (IReadOnlySet<int>)new HashSet<int>();

            // Local helper: find revision conflicts between remote items and protected local items
            async Task<List<(int Id, int LocalRev, int RemoteRev)>> FindConflictsAsync(
                IEnumerable<WorkItem> remoteItems)
            {
                var conflicts = new List<(int Id, int LocalRev, int RemoteRev)>();
                if (protectedIds.Count == 0) return conflicts;
                foreach (var remoteItem in remoteItems)
                {
                    if (!protectedIds.Contains(remoteItem.Id)) continue;
                    var localItem = await workItemRepo.GetByIdAsync(remoteItem.Id);
                    if (localItem is not null && remoteItem.Revision > localItem.Revision)
                        conflicts.Add((remoteItem.Id, localItem.Revision, remoteItem.Revision));
                }
                return conflicts;
            }

            // Local helper: print conflict details as informational warnings
            void PrintConflictWarnings(List<(int Id, int LocalRev, int RemoteRev)> conflicts)
            {
                Console.Error.WriteLine(fmt.FormatError("Warning: the following protected items have newer remote revisions (skipped):"));
                foreach (var (id, localRev, remoteRev) in conflicts)
                    Console.Error.WriteLine(fmt.FormatError($"  #{id}: local rev {localRev} → remote rev {remoteRev}"));
                Console.Error.WriteLine(fmt.FormatError("Run 'twig save' first, or use 'twig refresh --force' to overwrite."));
            }

            // Fetch all scopes first, then save through appropriate path.
            IReadOnlyList<WorkItem> sprintItems = [];
            WorkItem? activeItem = null;
            IReadOnlyList<WorkItem> childItems = [];
            var activeId = await contextStore.GetActiveWorkItemIdAsync();
            var fetched = 0;

            if (realIds.Count > 0)
            {
                sprintItems = await adoService.FetchBatchAsync(realIds);
                fetched = realIds.Count;
            }

            if (activeId.HasValue && activeId.Value > 0 && !realIds.Contains(activeId.Value))
            {
                activeItem = await adoService.FetchAsync(activeId.Value);
            }

            if (activeId.HasValue && activeId.Value > 0)
            {
                childItems = await adoService.FetchChildrenAsync(activeId.Value);
            }

            // Detect revision conflicts for informational output
            var allConflicts = new List<(int Id, int LocalRev, int RemoteRev)>();
            allConflicts.AddRange(await FindConflictsAsync(sprintItems));
            if (activeItem is not null)
                allConflicts.AddRange(await FindConflictsAsync([activeItem]));
            allConflicts.AddRange(await FindConflictsAsync(childItems));

            if (allConflicts.Count > 0)
                PrintConflictWarnings(allConflicts);

            // Persist fetched data: --force bypasses protection, otherwise use ProtectedCacheWriter
            if (force)
            {
                if (sprintItems.Count > 0)
                    await workItemRepo.SaveBatchAsync(sprintItems);
                if (activeItem is not null)
                    await workItemRepo.SaveAsync(activeItem);
                if (childItems.Count > 0)
                    await workItemRepo.SaveBatchAsync(childItems);
            }
            else
            {
                // Reuse the pre-computed protectedIds to avoid redundant SyncGuard queries
                if (sprintItems.Count > 0)
                    await protectedCacheWriter.SaveBatchProtectedAsync(sprintItems, protectedIds);
                if (activeItem is not null)
                    await protectedCacheWriter.SaveProtectedAsync(activeItem, protectedIds);
                if (childItems.Count > 0)
                    await protectedCacheWriter.SaveBatchProtectedAsync(childItems, protectedIds);
            }

            Console.WriteLine(fmt.FormatSuccess($"Refreshed {fetched} item(s)."));
        }

        // ITEM-155: Ancestor hydration — iteratively fetch orphan parent IDs not yet in cache.
        // Repeat up to 5 levels to walk from fetched items up to root epics.
        for (var level = 0; level < 5; level++)
        {
            var orphanIds = await workItemRepo.GetOrphanParentIdsAsync();
            if (orphanIds.Count == 0)
                break;

            var ancestors = await adoService.FetchBatchAsync(orphanIds);
            if (ancestors.Count == 0)
                break;

            await workItemRepo.SaveBatchAsync(ancestors);
        }

        // Sync working set after sprint item save (EPIC-004) — NO eviction (FR-013)
        var workingSet = await workingSetService.ComputeAsync(iteration);
        await syncCoordinator.SyncWorkingSetAsync(workingSet);

        // Refresh user display name if not yet set
        if (string.IsNullOrWhiteSpace(config.User.DisplayName))
        {
            try
            {
                var displayName = await iterationService.GetAuthenticatedUserDisplayNameAsync();
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    config.User.DisplayName = displayName;
                    Console.WriteLine(fmt.FormatInfo($"  User: {displayName}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine(fmt.FormatInfo($"⚠ Could not detect user identity: {ex.Message}"));
            }
        }

        // Fetch and persist type appearances (always, regardless of work item count)
        var appearances = await iterationService.GetWorkItemTypeAppearancesAsync();
        config.TypeAppearances = appearances
            .Select(a => new TypeAppearanceConfig { Name = a.Name, Color = a.Color ?? string.Empty, IconId = a.IconId })
            .ToList();
        await config.SaveAsync(paths.ConfigPath);

        // Fetch type state sequences and process configuration for the process_types table
        try
        {
            await ProcessTypeSyncService.SyncAsync(iterationService, processTypeStore);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(fmt.FormatInfo($"⚠ Could not fetch type data: {ex.Message}"));
        }

        // Update cache freshness timestamp so subsequent reads don't show stale indicators
        await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));

        if (promptStateWriter is not null) await promptStateWriter.WritePromptStateAsync();

        return 0;
    }
}
