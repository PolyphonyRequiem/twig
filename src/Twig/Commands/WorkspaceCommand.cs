using System.Globalization;
using System.Runtime.CompilerServices;
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
/// Implements <c>twig workspace [show]</c>, <c>twig show</c>, <c>twig ws</c>:
/// displays the current workspace including active context, sprint items, and seeds
/// with stale seed warnings.
/// When <c>--all</c> is specified (or via <c>twig sprint</c>), shows all team items
/// grouped by assignee instead of filtering to the current user.
/// </summary>
public sealed class WorkspaceCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IIterationService iterationService,
    TwigConfiguration config,
    OutputFormatterFactory formatterFactory,
    HintEngine hintEngine,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    ITrackingService trackingService,
    RenderingPipelineFactory? pipelineFactory = null)
{
    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool noRefresh = false, CancellationToken ct = default, bool sprintLayout = false)
    {
        var (fmt, renderer) = pipelineFactory is not null
            ? pipelineFactory.Resolve(outputFormat, noLive)
            : (formatterFactory.GetFormatter(outputFormat), null);

        if (renderer is not null && !all && !sprintLayout)
        {
            // NOTE: The async Spectre rendering path is gated by `!all`, so `isTeamView`
            // passed to RenderWorkspaceAsync is always false at runtime. The team/sprint
            // view (--all) always falls through to ExecuteSyncAsync. The Spectre team-view
            // table and assignee column handling is reserved for a future async team-view path.
            Domain.Aggregates.WorkItem? contextItem = null;
            IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems = Array.Empty<Domain.Aggregates.WorkItem>();
            IReadOnlyList<Domain.Aggregates.WorkItem> seeds = Array.Empty<Domain.Aggregates.WorkItem>();

            // Load tracking overlay (tracked items + excluded IDs)
            var trackedItems = await trackingService.GetTrackedItemsAsync(ct);
            var excludedIds = await trackingService.GetExcludedIdsAsync(ct);

            // Wire working level into SpectreRenderer for future tree-based workspace rendering
            if (renderer is SpectreRenderer spectreRenderer)
            {
                var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
                if (processConfig is not null)
                {
                    spectreRenderer.TypeLevelMap = Domain.Services.BacklogHierarchyService.GetTypeLevelMap(processConfig);
                    spectreRenderer.WorkingLevelTypeName = config.Workspace.WorkingLevel;
                }

                // Expose tracked item IDs so the renderer can show pinned markers
                spectreRenderer.TrackedItemIds = new HashSet<int>(trackedItems.Select(t => t.WorkItemId));
            }

            // Resolve dynamic columns before rendering (EPIC-004)
            // NOTE: sprintItems is intentionally omitted here — in the live Spectre streaming path,
            // items arrive progressively, so fill-rate auto-discovery is unavailable. Only config-
            // specified columns appear. The sync path (JSON/--no-live) supplies sprintItems for
            // auto-discovery after all items are loaded.
            var dynamicColumns = await ResolveDynamicColumnsAsync(all ? "sprint" : "workspace", isJsonOutput: false, ct: ct);

            async IAsyncEnumerable<WorkspaceDataChunk> StreamWorkspaceData(
                [EnumeratorCancellation] CancellationToken ct)
            {
                // Stage 1: Context — auto-fetch on cache miss via ActiveItemResolver (G-3)
                var activeId = await contextStore.GetActiveWorkItemIdAsync(ct);
                if (activeId.HasValue)
                {
                    var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value, ct);
                    resolveResult.TryGetWorkItem(out contextItem, out _, out _);
                }
                yield return new WorkspaceDataChunk.ContextLoaded(contextItem);

                // Stage 2: Sprint items
                var iteration = await iterationService.GetCurrentIterationAsync(ct);
                var userDisplayName = config.User.DisplayName;
                if (!string.IsNullOrWhiteSpace(userDisplayName))
                    sprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(iteration, userDisplayName, ct);
                else
                    sprintItems = await workItemRepo.GetByIterationAsync(iteration, ct);
                yield return new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, WorkspaceSections.Build(sprintItems, excludedIds: excludedIds));

                // Stage 3: Seeds
                seeds = await workItemRepo.GetSeedsAsync(ct);
                yield return new WorkspaceDataChunk.SeedsLoaded(seeds);

                // Stage 4: Check cache freshness for stale-while-revalidate (EPIC-006)
                // Skipped entirely when --no-refresh is specified
                if (!noRefresh)
                {
                    var lastRefreshedRaw = await contextStore.GetValueAsync("last_refreshed_at", ct);
                    if (IsCacheStale(lastRefreshedRaw, config.Display.CacheStaleMinutes))
                    {
                        yield return new WorkspaceDataChunk.RefreshStarted();

                        // Cannot yield inside try/catch in C# iterators — collect results first.
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSprintItems = null;
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSeeds = null;
                        bool refreshFailed = false;

                        try
                        {
                            // Re-fetch sprint items via fresh ADO iteration resolution
                            var freshIteration = await iterationService.GetCurrentIterationAsync(ct);
                            if (!string.IsNullOrWhiteSpace(userDisplayName))
                                refreshedSprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(freshIteration, userDisplayName, ct);
                            else
                                refreshedSprintItems = await workItemRepo.GetByIterationAsync(freshIteration, ct);

                            // Re-fetch seeds
                            refreshedSeeds = await workItemRepo.GetSeedsAsync(ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Re-fetch failed (network timeout, auth failure, etc.) —
                            // fall back to original data so the renderer shows stale rows rather than an empty table.
                            refreshFailed = true;
                        }

                        if (!refreshFailed && refreshedSprintItems is not null && refreshedSeeds is not null)
                        {
                            // Update closure variables so hint computation uses refreshed data
                            sprintItems = refreshedSprintItems;
                            seeds = refreshedSeeds;

                            // Update freshness timestamp (best-effort — persistence failure must not discard fetched data)
                            try { await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"), ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort; data display is unaffected */ }
                        }

                        // Yield data rows (refreshed on success, original on failure)
                        yield return new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, WorkspaceSections.Build(sprintItems, excludedIds: excludedIds));
                        yield return new WorkspaceDataChunk.SeedsLoaded(seeds);
                        yield return new WorkspaceDataChunk.RefreshCompleted();
                    }
                }
            }

            await renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), config.Seed.StaleDays, all, ct, dynamicColumns, config.Display.CacheStaleMinutes);

            // Build Workspace from closure-populated variables for hint computation
            var workspace = Workspace.Build(contextItem, sprintItems, seeds,
                sections: WorkspaceSections.Build(sprintItems, excludedIds: excludedIds),
                trackedItems: trackedItems,
                excludedIds: excludedIds);

            var hints = hintEngine.GetHints("workspace",
                workspace: workspace,
                outputFormat: outputFormat);
            renderer.RenderHints(hints);

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, --all, sprint, piped output)
        return await ExecuteSyncAsync(fmt, all, sprintLayout);
    }

    private async Task<int> ExecuteSyncAsync(IOutputFormatter fmt, bool all, bool sprintLayout = false)
    {
        // Get active context (nullable) — auto-fetch on cache miss via ActiveItemResolver
        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        Domain.Aggregates.WorkItem? contextItem = null;
        if (activeId.HasValue)
        {
            var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value);
            resolveResult.TryGetWorkItem(out contextItem, out _, out _);
        }

        // Get current iteration items — scoped to user by default, all team items with --all
        var iteration = await iterationService.GetCurrentIterationAsync();
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems;

        var userDisplayName = config.User.DisplayName;
        if (!all && !string.IsNullOrWhiteSpace(userDisplayName))
        {
            sprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(iteration, userDisplayName);
        }
        else
        {
            sprintItems = await workItemRepo.GetByIterationAsync(iteration);
        }

        // Get seeds
        var seeds = await workItemRepo.GetSeedsAsync();

        // Load tracking overlay (tracked items + excluded IDs)
        var trackedItems = await trackingService.GetTrackedItemsAsync();
        var excludedIds = await trackingService.GetExcludedIdsAsync();

        // Resolve dynamic columns (EPIC-004)
        var isJsonOutput = fmt is JsonOutputFormatter;
        var viewName = all ? "sprint" : "workspace";
        var dynamicColumns = await ResolveDynamicColumnsAsync(viewName, isJsonOutput, sprintItems: sprintItems);
        if (fmt is JsonOutputFormatter jsonFmt)
            jsonFmt.DynamicColumns = dynamicColumns;

        // Update freshness timestamp (sync path also tracks cache freshness)
        await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));

        // Build hierarchy when sprint items exist
        SprintHierarchy? hierarchy = null;
        if (sprintItems.Count > 0)
        {
            var uniqueParentIds = new HashSet<int>();
            foreach (var item in sprintItems)
            {
                if (item.ParentId.HasValue)
                    uniqueParentIds.Add(item.ParentId.Value);
            }

            var parentLookup = new Dictionary<int, Domain.Aggregates.WorkItem>();
            foreach (var parentId in uniqueParentIds)
            {
                var chain = await workItemRepo.GetParentChainAsync(parentId);
                foreach (var chainItem in chain)
                    parentLookup.TryAdd(chainItem.Id, chainItem);
            }

            // Read cached process configuration (no network call)
            var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
            if (processConfig is not null)
            {
                var typeNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in sprintItems)
                    typeNameSet.Add(item.Type.Value);

                var ceilingTypeNames = CeilingComputer.Compute(new List<string>(typeNameSet), processConfig);
                var typeLevelMap = Domain.Services.BacklogHierarchyService.GetTypeLevelMap(processConfig);
                hierarchy = SprintHierarchy.Build(sprintItems, parentLookup, ceilingTypeNames, typeLevelMap);
            }
        }

        var workspace = Workspace.Build(contextItem, sprintItems, seeds, hierarchy,
            sections: WorkspaceSections.Build(sprintItems, excludedIds: excludedIds),
            trackedItems: trackedItems,
            excludedIds: excludedIds);

        if (all || sprintLayout)
        {
            Console.WriteLine(fmt.FormatSprintView(workspace, config.Seed.StaleDays));
        }
        else
        {
            Console.WriteLine(fmt.FormatWorkspace(workspace, config.Seed.StaleDays));
        }

        // Dirty orphans: items with unsaved changes not in sprint/seed scope (EPIC-004)
        if (!all && fmt is not JsonOutputFormatter && fmt is not MinimalOutputFormatter)
        {
            var workingSet = await workingSetService.ComputeAsync(iteration);
            if (workingSet.DirtyItemIds.Count > 0)
            {
                var sprintItemIds = new HashSet<int>(sprintItems.Select(s => s.Id));
                var seedIds = new HashSet<int>(seeds.Select(s => s.Id));
                var orphanIds = new List<int>();
                foreach (var dirtyId in workingSet.DirtyItemIds)
                {
                    if (!sprintItemIds.Contains(dirtyId) && !seedIds.Contains(dirtyId))
                        orphanIds.Add(dirtyId);
                }

                if (orphanIds.Count > 0)
                {
                    var orphanItems = new List<Domain.Aggregates.WorkItem>();
                    foreach (var orphanId in orphanIds)
                    {
                        var orphanItem = await workItemRepo.GetByIdAsync(orphanId);
                        if (orphanItem is not null)
                            orphanItems.Add(orphanItem);
                    }

                    if (orphanItems.Count > 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine(fmt.FormatInfo("Unsaved changes:"));
                        foreach (var orphan in orphanItems)
                            Console.WriteLine(fmt.FormatWorkItem(orphan, showDirty: true));
                        Console.WriteLine(fmt.FormatHint("Run 'twig save' to push these changes."));
                    }
                }
            }
        }

        var hints = hintEngine.GetHints("workspace",
            workspace: workspace,
            outputFormat: fmt is JsonOutputFormatter or JsonCompactOutputFormatter ? "json" : (fmt is MinimalOutputFormatter ? "minimal" : "human"));
        foreach (var hint in hints)
        {
            var formatted = fmt.FormatHint(hint);
            if (!string.IsNullOrEmpty(formatted))
                Console.WriteLine(formatted);
        }

        return 0;
    }

    /// <summary>
    /// Determines whether the cache is stale based on the <c>last_refreshed_at</c> timestamp.
    /// Returns <c>true</c> if no timestamp exists, the timestamp cannot be parsed,
    /// or the timestamp is older than <paramref name="cacheStaleMinutes"/> minutes.
    /// </summary>
    internal static bool IsCacheStale(string? lastRefreshedRaw, int cacheStaleMinutes)
    {
        if (lastRefreshedRaw is null)
            return true;
        if (!DateTimeOffset.TryParse(lastRefreshedRaw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var lastRefreshed))
            return true;
        return lastRefreshed < DateTimeOffset.UtcNow.AddMinutes(-cacheStaleMinutes);
    }

    /// <summary>
    /// Resolves dynamic columns for the workspace/sprint table (EPIC-004).
    /// Uses config overrides when specified, otherwise auto-discovers from field fill rates.
    /// </summary>
    private async Task<IReadOnlyList<Domain.ValueObjects.ColumnSpec>> ResolveDynamicColumnsAsync(
        string viewName,
        bool isJsonOutput,
        IReadOnlyList<Domain.Aggregates.WorkItem>? sprintItems = null,
        CancellationToken ct = default)
    {
        // Check for config-specified columns
        var configuredColumns = viewName.Equals("sprint", StringComparison.OrdinalIgnoreCase)
            ? config.Display.Columns?.Sprint
            : config.Display.Columns?.Workspace;

        // Load cached field definitions (may be empty if not yet synced)
        var fieldDefs = await fieldDefinitionStore.GetAllAsync(ct);

        // If config specifies columns, use them directly (skip auto-discovery)
        if (configuredColumns is { Count: > 0 })
        {
            return Domain.Services.ColumnResolver.Resolve(
                Array.Empty<Domain.ValueObjects.FieldProfile>(),
                fieldDefs,
                configuredColumns,
                config.Display.FillRateThreshold,
                config.Display.MaxExtraColumns,
                isJsonOutput);
        }

        // Auto-discover from items if available
        if (sprintItems is null || sprintItems.Count == 0)
            return Array.Empty<Domain.ValueObjects.ColumnSpec>();

        var profiles = Domain.Services.FieldProfileService.ComputeProfiles(sprintItems);
        return Domain.Services.ColumnResolver.Resolve(
            profiles,
            fieldDefs,
            configuredColumns: null,
            config.Display.FillRateThreshold,
            config.Display.MaxExtraColumns,
            isJsonOutput);
    }
}
