using System.Globalization;
using System.Runtime.CompilerServices;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig workspace [show]</c>, <c>twig show</c>, <c>twig ws</c>:
/// displays the current workspace including active context, sprint items, and seeds
/// with stale seed warnings.
/// When <c>--all</c> is specified (or via <c>twig sprint</c>), shows all team items
/// grouped by assignee instead of filtering to the current user.
/// </summary>
/// <remarks>
/// Partially migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: <c>json</c>, <c>jsonc</c>, <c>minimal</c>, and <c>ids</c> output formats now project
/// the workspace through a <see cref="Twig.RenderTree.RenderTree"/>. The <c>human</c> path
/// continues to delegate to <see cref="HumanOutputFormatter.FormatWorkspace"/> /
/// <see cref="HumanOutputFormatter.FormatSprintView"/> so the rich human-format
/// rendering (active markers, dirty/stale glyphs, tree layout) stays intact.
/// </remarks>
public sealed class WorkspaceCommand(
    CommandContext ctx,
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    IIterationService iterationService,
    IProcessTypeStore processTypeStore,
    IFieldDefinitionStore fieldDefinitionStore,
    ActiveItemResolver activeItemResolver,
    WorkingSetService workingSetService,
    ITrackingService trackingService,
    ISprintHierarchyBuilder sprintHierarchyBuilder,
    SprintIterationResolver sprintIterationResolver,
    TreeRenderingService? treeRenderingService = null,
    SyncCoordinatorFactory? syncCoordinatorFactory = null,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool noRefresh = false, CancellationToken ct = default, bool sprintLayout = false, bool flat = false, bool tree = false)
    {
        if (tree && flat)
        {
            Console.Error.WriteLine("error: --tree and --flat are mutually exclusive.");
            return 1;
        }

        if (tree)
        {
            return await ExecuteTreeModeAsync(outputFormat, all, noLive, noRefresh, ct);
        }

        var (fmt, renderer) = ctx.Resolve(outputFormat, noLive);

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

            // Wire working level and tree rendering into SpectreRenderer
            if (renderer is SpectreRenderer spectreRenderer)
            {
                var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
                if (processConfig is not null)
                {
                    spectreRenderer.TypeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
                    spectreRenderer.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;

                    // Enable tree rendering when process configuration is available and --flat is not specified
                    spectreRenderer.UseTreeRendering = !flat;
                    spectreRenderer.TreeDepthUp = ctx.Config.Display.TreeDepthUp;
                    spectreRenderer.TreeDepthDown = ctx.Config.Display.TreeDepthDown;
                    spectreRenderer.TreeDepthSideways = ctx.Config.Display.TreeDepthSideways;
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
                yield return new ContextLoaded(contextItem);

                // Stage 2: Sprint items — use configured sprints when available, else fall back to current iteration
                var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
                var userDisplayName = ctx.Config.User.DisplayName;
                if (resolvedIterations.Count > 0)
                {
                    sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                        resolvedIterations, userDisplayName, allUsers: false, ct);
                }
                else
                {
                    var iteration = await iterationService.GetCurrentIterationAsync(ct);
                    if (!string.IsNullOrWhiteSpace(userDisplayName))
                        sprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(iteration, userDisplayName, ct);
                    else
                        sprintItems = await workItemRepo.GetByIterationAsync(iteration, ct);
                }
                var treeRoots = await BuildTreeRootsAsync(sprintItems, ct);
                yield return new SprintItemsLoaded(sprintItems, WorkspaceSections.Build(sprintItems, excludedIds: excludedIds, treeRoots: treeRoots));

                // Stage 3: Seeds
                seeds = await workItemRepo.GetSeedsAsync(ct);
                yield return new SeedsLoaded(seeds);

                // Stage 4: Check cache freshness for stale-while-revalidate (EPIC-006)
                // Skipped entirely when --no-refresh is specified
                if (!noRefresh)
                {
                    var lastRefreshedRaw = await contextStore.GetValueAsync("last_refreshed_at", ct);
                    if (IsCacheStale(lastRefreshedRaw, ctx.Config.Display.CacheStaleMinutes))
                    {
                        yield return new RefreshStarted();

                        // Cannot yield inside try/catch in C# iterators — collect results first.
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSprintItems = null;
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSeeds = null;
                        bool refreshFailed = false;

                        try
                        {
                            // Re-fetch sprint items using configured sprints or current iteration
                            var freshIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
                            if (freshIterations.Count > 0)
                            {
                                refreshedSprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                                    freshIterations, userDisplayName, allUsers: false, ct);
                            }
                            else
                            {
                                var freshIteration = await iterationService.GetCurrentIterationAsync(ct);
                                if (!string.IsNullOrWhiteSpace(userDisplayName))
                                    refreshedSprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(freshIteration, userDisplayName, ct);
                                else
                                    refreshedSprintItems = await workItemRepo.GetByIterationAsync(freshIteration, ct);
                            }

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
                        var refreshTreeRoots = await BuildTreeRootsAsync(sprintItems, ct);
                        yield return new SprintItemsLoaded(sprintItems, WorkspaceSections.Build(sprintItems, excludedIds: excludedIds, treeRoots: refreshTreeRoots));
                        yield return new SeedsLoaded(seeds);
                        yield return new RefreshCompleted();
                    }
                }
            }

            await renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), ctx.Config.Seed.StaleDays, all, ct, dynamicColumns, ctx.Config.Display.CacheStaleMinutes);

            // Build Workspace from closure-populated variables for hint computation
            var workspace = Workspace.Build(contextItem, sprintItems, seeds,
                sections: WorkspaceSections.Build(sprintItems, excludedIds: excludedIds),
                trackedItems: trackedItems,
                excludedIds: excludedIds);

            var hints = ctx.HintEngine.GetHints("workspace",
                workspace: workspace,
                outputFormat: outputFormat);
            renderer.RenderHints(hints);

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, --all, sprint, piped output)
        return await ExecuteSyncAsync(fmt, outputFormat, all, noRefresh, sprintLayout, flat);
    }

    /// <summary>
    /// Full-backlog tree mode: renders each sprint item as an independent tree root
    /// expanded to the configured depth. Delegates to <see cref="TreeRenderingService"/>
    /// for per-item rendering so all output formats (human, json, minimal) work consistently.
    /// </summary>
    private async Task<int> ExecuteTreeModeAsync(string outputFormat, bool all, bool noLive, bool noRefresh, CancellationToken ct)
    {
        if (treeRenderingService is null)
        {
            Console.Error.WriteLine("error: Tree rendering is not available.");
            return 1;
        }

        // Gather sprint items using the same logic as the sync path
        var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
        var userDisplayName = ctx.Config.User.DisplayName;
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems;

        if (resolvedIterations.Count > 0)
        {
            sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                resolvedIterations, userDisplayName, allUsers: all, ct);
        }
        else
        {
            var iteration = await iterationService.GetCurrentIterationAsync(ct);
            if (!all && !string.IsNullOrWhiteSpace(userDisplayName))
                sprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(iteration, userDisplayName, ct);
            else
                sprintItems = await workItemRepo.GetByIterationAsync(iteration, ct);
        }

        if (sprintItems.Count == 0)
        {
            var (emptyFmt, _) = ctx.Resolve(outputFormat, noLive: true);
            Console.Error.WriteLine(emptyFmt.FormatInfo("No sprint items found."));
            return 0;
        }

        // Render a tree for each sprint root item.
        // Only allow sync/refresh on the first item to avoid redundant network calls.
        for (var i = 0; i < sprintItems.Count; i++)
        {
            var itemNoRefresh = noRefresh || i > 0;
            var result = await treeRenderingService.RenderTreeAsync(
                sprintItems[i].Id, outputFormat, depth: null, noLive, itemNoRefresh, ct);
            if (result != 0) return result;
        }

        return 0;
    }

    private async Task<int> ExecuteSyncAsync(IOutputFormatter fmt, string outputFormat, bool all, bool noRefresh = false, bool sprintLayout = false, bool flat = false)
    {
        // Sync-first for machine formats: ensure consumers get fresh data.
        // The human (TTY) path handles sync via the live streaming path above.
        var isMachineFormat = IsMachineFormat(outputFormat);
        if (isMachineFormat && !noRefresh && syncCoordinatorFactory is not null)
        {
            try
            {
                var resolvedIters = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints);
                var workingSet = await workingSetService.ComputeAsync(resolvedIters.Count > 0 ? resolvedIters : null);
                await syncCoordinatorFactory.ReadOnly.SyncWorkingSetAsync(workingSet);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sync failure is non-fatal — fall through to emit cache-only data
            }
        }

        // Get active context (nullable) — auto-fetch on cache miss via ActiveItemResolver
        var activeId = await contextStore.GetActiveWorkItemIdAsync();
        Domain.Aggregates.WorkItem? contextItem = null;
        if (activeId.HasValue)
        {
            var resolveResult = await activeItemResolver.ResolveByIdAsync(activeId.Value);
            resolveResult.TryGetWorkItem(out contextItem, out _, out _);
        }

        // Get sprint items — use configured sprints when available, else fall back to current iteration
        var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints);
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems;

        var userDisplayName = ctx.Config.User.DisplayName;
        if (resolvedIterations.Count > 0)
        {
            sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                resolvedIterations, userDisplayName, allUsers: all);
        }
        else
        {
            var iteration = await iterationService.GetCurrentIterationAsync();
            if (!all && !string.IsNullOrWhiteSpace(userDisplayName))
            {
                sprintItems = await workItemRepo.GetByIterationAndAssigneeAsync(iteration, userDisplayName);
            }
            else
            {
                sprintItems = await workItemRepo.GetByIterationAsync(iteration);
            }
        }

        // Get seeds
        var seeds = await workItemRepo.GetSeedsAsync();

        // Load tracking overlay (tracked items + excluded IDs)
        var trackedItems = await trackingService.GetTrackedItemsAsync();
        var excludedIds = await trackingService.GetExcludedIdsAsync();

        // Resolve dynamic columns (EPIC-004)
        var isJsonOutput = IsJsonFormat(outputFormat);
        var viewName = all ? "sprint" : "workspace";
        var dynamicColumns = await ResolveDynamicColumnsAsync(viewName, isJsonOutput, sprintItems: sprintItems);

        // Update freshness timestamp (sync path also tracks cache freshness)
        await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"));

        // Build hierarchy when sprint items exist
        SprintHierarchy? hierarchy = null;
        IReadOnlyList<SprintHierarchyNode>? treeRoots = null;
        IReadOnlyDictionary<string, int>? typeLevelMap = null;
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
                typeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
                hierarchy = sprintHierarchyBuilder.Build(sprintItems, parentLookup, ceilingTypeNames, typeLevelMap);

                // Extract tree roots from hierarchy for tree-based rendering
                var roots = new List<SprintHierarchyNode>();
                foreach (var group in hierarchy.AssigneeGroups.Values)
                    foreach (var node in group)
                        roots.Add(node);
                treeRoots = roots.Count > 0 ? roots : null;
            }
        }

        // Wire tree rendering config into HumanOutputFormatter (mirrors SpectreRenderer wiring)
        if (fmt is HumanOutputFormatter humanFmt && typeLevelMap is not null)
        {
            humanFmt.TypeLevelMap = typeLevelMap;
            humanFmt.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;
            humanFmt.UseTreeRendering = !flat;
            humanFmt.TreeDepthUp = ctx.Config.Display.TreeDepthUp;
            humanFmt.TreeDepthDown = ctx.Config.Display.TreeDepthDown;
            humanFmt.TreeDepthSideways = ctx.Config.Display.TreeDepthSideways;
        }

        var workspace = Workspace.Build(contextItem, sprintItems, seeds, hierarchy,
            sections: WorkspaceSections.Build(sprintItems, excludedIds: excludedIds, treeRoots: treeRoots),
            trackedItems: trackedItems,
            excludedIds: excludedIds);

        if (fmt is HumanOutputFormatter human)
        {
            if (all || sprintLayout)
            {
                Console.WriteLine(human.FormatSprintView(workspace, ctx.Config.Seed.StaleDays));
            }
            else
            {
                Console.WriteLine(human.FormatWorkspace(workspace, ctx.Config.Seed.StaleDays));
            }
        }
        else
        {
            RenderWorkspaceAsTree(workspace, outputFormat, useSprintLayout: all || sprintLayout, ctx.Config.Seed.StaleDays, dynamicColumns);
        }

        // Dirty orphans: items with unsaved changes not in sprint/seed scope (EPIC-004)
        if (!all && !isMachineFormat)
        {
            // Use resolved iterations for dirty orphan scope; fall back to current iteration
            IReadOnlyList<IterationPath> orphanIterations = resolvedIterations;
            if (orphanIterations.Count == 0)
            {
                var fallbackIteration = await iterationService.GetCurrentIterationAsync();
                orphanIterations = [fallbackIteration];
            }
            var workingSet = await workingSetService.ComputeAsync(orphanIterations);
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
                        Console.WriteLine("Unsaved changes:");
                        foreach (var orphan in orphanItems)
                            Console.WriteLine($"  #{orphan.Id} {orphan.Type} — {orphan.Title} [{orphan.State}] *");
                        Console.WriteLine("Run 'twig save' to push these changes.");
                    }
                }
            }
        }

        var hints = ctx.HintEngine.GetHints("workspace",
            workspace: workspace,
            outputFormat: NormalizeOutputFormat(outputFormat));
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

    private static bool IsMachineFormat(string outputFormat) =>
        (outputFormat ?? string.Empty).ToLowerInvariant() is "json" or "json-full" or "json-compact" or "minimal" or "ids";

    private static bool IsJsonFormat(string outputFormat) =>
        (outputFormat ?? string.Empty).ToLowerInvariant() is "json" or "json-full";

    private static string NormalizeOutputFormat(string outputFormat) =>
        (outputFormat ?? string.Empty).ToLowerInvariant() switch
        {
            "json" or "json-full" or "json-compact" => "json",
            "minimal" => "minimal",
            "ids" => "ids",
            _ => "human",
        };

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
            ? ctx.Config.Display.Columns?.Sprint
            : ctx.Config.Display.Columns?.Workspace;

        // Load cached field definitions (may be empty if not yet synced)
        var fieldDefs = await fieldDefinitionStore.GetAllAsync(ct);

        // If config specifies columns, use them directly (skip auto-discovery)
        if (configuredColumns is { Count: > 0 })
        {
            return Domain.Services.Workspace.ColumnResolver.Resolve(
                Array.Empty<Domain.ValueObjects.FieldProfile>(),
                fieldDefs,
                configuredColumns,
                ctx.Config.Display.FillRateThreshold,
                ctx.Config.Display.MaxExtraColumns,
                isJsonOutput);
        }

        // Auto-discover from items if available
        if (sprintItems is null || sprintItems.Count == 0)
            return Array.Empty<Domain.ValueObjects.ColumnSpec>();

        var profiles = FieldProfileService.ComputeProfiles(sprintItems);
        return Domain.Services.Workspace.ColumnResolver.Resolve(
            profiles,
            fieldDefs,
            configuredColumns: null,
            ctx.Config.Display.FillRateThreshold,
            ctx.Config.Display.MaxExtraColumns,
            isJsonOutput);
    }

    /// <summary>
    /// Builds flattened tree roots from sprint items by walking parent chains and
    /// assembling a <see cref="SprintHierarchy"/>. Used by the live async streaming
    /// path to provide hierarchy data for tree-based workspace rendering.
    /// </summary>
    private async Task<IReadOnlyList<SprintHierarchyNode>?> BuildTreeRootsAsync(
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems, CancellationToken ct = default)
    {
        if (sprintItems.Count == 0)
            return null;

        var uniqueParentIds = new HashSet<int>();
        foreach (var item in sprintItems)
        {
            if (item.ParentId.HasValue)
                uniqueParentIds.Add(item.ParentId.Value);
        }

        var parentLookup = new Dictionary<int, Domain.Aggregates.WorkItem>();
        foreach (var parentId in uniqueParentIds)
        {
            var chain = await workItemRepo.GetParentChainAsync(parentId, ct);
            foreach (var chainItem in chain)
                parentLookup.TryAdd(chainItem.Id, chainItem);
        }

        var processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
        if (processConfig is null)
            return null;

        var typeNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sprintItems)
            typeNameSet.Add(item.Type.Value);

        var ceilingTypeNames = CeilingComputer.Compute(new List<string>(typeNameSet), processConfig);
        var typeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
        var hierarchy = sprintHierarchyBuilder.Build(sprintItems, parentLookup, ceilingTypeNames, typeLevelMap);

        // Flatten all assignee groups for personal workspace display
        var roots = new List<SprintHierarchyNode>();
        foreach (var group in hierarchy.AssigneeGroups.Values)
        {
            foreach (var node in group)
                roots.Add(node);
        }

        return roots.Count > 0 ? roots : null;
    }

    /// <summary>
    /// Resolves configured sprint expressions to concrete <see cref="IterationPath"/> values.
    /// Returns an empty list when no sprints are configured or none resolve successfully.
    /// </summary>
    private async Task<IReadOnlyList<IterationPath>> ResolveSprintIterationsAsync(
        List<SprintEntry>? sprintEntries, CancellationToken ct = default)
    {
        if (sprintEntries is null or { Count: 0 })
            return [];

        var expressions = new List<IterationExpression>(sprintEntries.Count);
        foreach (var entry in sprintEntries)
        {
            var parseResult = IterationExpression.Parse(entry.Expression);
            if (parseResult.IsSuccess)
                expressions.Add(parseResult.Value);
        }

        if (expressions.Count == 0)
            return [];

        return await sprintIterationResolver.ResolveAllAsync(expressions, ct);
    }

    /// <summary>
    /// Fetches work items across all resolved iterations, deduplicated by work item ID.
    /// When <paramref name="allUsers"/> is <c>false</c> and a display name is available,
    /// items are scoped to the configured user.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Aggregates.WorkItem>> GetSprintItemsFromResolvedIterationsAsync(
        IReadOnlyList<IterationPath> resolvedIterations,
        string? userDisplayName,
        bool allUsers,
        CancellationToken ct = default)
    {
        var seenIds = new HashSet<int>();
        var result = new List<Domain.Aggregates.WorkItem>();

        foreach (var path in resolvedIterations)
        {
            var items = allUsers || string.IsNullOrWhiteSpace(userDisplayName)
                ? await workItemRepo.GetByIterationAsync(path, ct)
                : await workItemRepo.GetByIterationAndAssigneeAsync(path, userDisplayName, ct);

            foreach (var item in items)
            {
                if (seenIds.Add(item.Id))
                    result.Add(item);
            }
        }

        return result;
    }

    // ── RenderTree projection for machine output formats (json/jsonc/minimal/ids) ────────
    // The human path keeps using HumanOutputFormatter to preserve rich-format behavior
    // (active marker, dirty/stale glyphs, tree layout). These helpers produce the
    // structural projection consumed by the JSON / minimal / ids renderers.

    private void RenderWorkspaceAsTree(
        Workspace workspace,
        string outputFormat,
        bool useSprintLayout,
        int staleDays,
        IReadOnlyList<ColumnSpec>? dynamicColumns)
    {
        var doc = useSprintLayout
            ? BuildSprintViewDocument(workspace, dynamicColumns)
            : BuildWorkspaceDocument(workspace, staleDays, dynamicColumns);

        var tree = new Twig.RenderTree.RenderTree([doc]);
        _rendererFactory.GetRenderer(outputFormat).Render(tree);
        Console.WriteLine();
    }

    private static RenderNode.Document BuildWorkspaceDocument(
        Workspace workspace,
        int staleDays,
        IReadOnlyList<ColumnSpec>? dynamicColumns)
    {
        var fields = new List<DocumentField>();

        // context: nested record or null KeyValue
        fields.Add(workspace.ContextItem is not null
            ? new DocumentField("context", BuildWorkItemRecord(workspace.ContextItem, dynamicColumns))
            : new DocumentField("context", new RenderNode.KeyValue("context", new RenderCell(string.Empty, new RenderValue.Null()))));

        fields.Add(new DocumentField("sprintItems", BuildWorkItemSection(workspace.SprintItems, dynamicColumns)));
        fields.Add(new DocumentField("seeds", BuildWorkItemSection(workspace.Seeds, dynamicColumns)));

        var staleSeeds = workspace.GetStaleSeeds(staleDays);
        fields.Add(new DocumentField("staleSeeds", BuildIdSection(staleSeeds.Select(s => s.Id))));

        if (workspace.Sections is not null)
        {
            fields.Add(new DocumentField("sections", BuildSectionsNode(workspace.Sections)));
            fields.Add(new DocumentField("excludedItemIds", BuildIdSection(workspace.Sections.ExcludedItemIds)));
        }

        if (workspace.TrackedItems.Count > 0)
            fields.Add(new DocumentField("trackedItems", BuildTrackedItemsSection(workspace.TrackedItems)));

        if (workspace.ExcludedIds.Count > 0)
            fields.Add(new DocumentField("excludedIds", BuildIdSection(workspace.ExcludedIds)));

        var dirtyCount = workspace.GetDirtyItems().Count;
        fields.Add(new DocumentField("dirtyCount", new RenderNode.KeyValue("dirtyCount", RenderCell.Integer(dirtyCount))));

        return new RenderNode.Document("workspace", fields);
    }

    private static RenderNode.Document BuildSprintViewDocument(
        Workspace workspace,
        IReadOnlyList<ColumnSpec>? dynamicColumns)
    {
        var fields = new List<DocumentField>();

        fields.Add(workspace.ContextItem is not null
            ? new DocumentField("context", BuildWorkItemRecord(workspace.ContextItem, dynamicColumns))
            : new DocumentField("context", new RenderNode.KeyValue("context", new RenderCell(string.Empty, new RenderValue.Null()))));

        // sprintByAssignee: a nested Document where each field is one assignee key
        // mapping to a Section of work-item records.
        var grouped = new Dictionary<string, List<Domain.Aggregates.WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in workspace.SprintItems)
        {
            var assignee = item.AssignedTo ?? string.Empty;
            if (!grouped.TryGetValue(assignee, out var list))
            {
                list = new List<Domain.Aggregates.WorkItem>();
                grouped[assignee] = list;
            }
            list.Add(item);
        }
        var assigneeFields = new List<DocumentField>();
        foreach (var kvp in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            assigneeFields.Add(new DocumentField(kvp.Key, BuildWorkItemSection(kvp.Value, dynamicColumns)));
        }
        fields.Add(new DocumentField("sprintByAssignee", new RenderNode.Document(null, assigneeFields)));

        fields.Add(new DocumentField("totalSprintItems", new RenderNode.KeyValue("totalSprintItems", RenderCell.Integer(workspace.SprintItems.Count))));
        fields.Add(new DocumentField("seeds", BuildWorkItemSection(workspace.Seeds, dynamicColumns)));

        var dirtyCount = workspace.GetDirtyItems().Count;
        fields.Add(new DocumentField("dirtyCount", new RenderNode.KeyValue("dirtyCount", RenderCell.Integer(dirtyCount))));

        return new RenderNode.Document("sprintView", fields);
    }

    private static RenderNode.Section BuildWorkItemSection(
        IReadOnlyList<Domain.Aggregates.WorkItem> items,
        IReadOnlyList<ColumnSpec>? dynamicColumns)
    {
        var children = new List<RenderNode>(items.Count);
        foreach (var item in items)
            children.Add(BuildWorkItemRecord(item, dynamicColumns));
        return new RenderNode.Section(null, children);
    }

    private static RenderNode.Record BuildWorkItemRecord(
        Domain.Aggregates.WorkItem item,
        IReadOnlyList<ColumnSpec>? dynamicColumns)
    {
        var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(item.Id),
            ["title"] = RenderCell.String(item.Title ?? string.Empty),
            ["type"] = RenderCell.String(item.Type.ToString()),
            ["state"] = RenderCell.String(item.State ?? string.Empty),
            ["assignedTo"] = RenderCell.String(item.AssignedTo ?? string.Empty),
            ["isDirty"] = RenderCell.Boolean(item.IsDirty),
            ["isSeed"] = RenderCell.Boolean(item.IsSeed),
            ["parentId"] = item.ParentId.HasValue
                ? RenderCell.Integer(item.ParentId.Value)
                : new RenderCell(string.Empty, new RenderValue.Null()),
            ["tags"] = RenderCell.String(GetTags(item)),
        };

        // Inline dynamic column values when supplied; otherwise flatten populated
        // fields (excluding System.Tags which is already promoted above). Cell keys
        // are the ADO reference names so JSON/minimal consumers can address them
        // directly.
        if (dynamicColumns is { Count: > 0 })
        {
            foreach (var col in dynamicColumns)
            {
                item.Fields.TryGetValue(col.ReferenceName, out var rawValue);
                var formatted = FormatterHelpers.FormatFieldValueForJson(rawValue, col.DataType);
                cells[col.ReferenceName] = RenderCell.String(formatted ?? string.Empty);
            }
        }
        else
        {
            foreach (var (refName, value) in item.Fields)
            {
                if (string.IsNullOrEmpty(value))
                    continue;
                if (string.Equals(refName, "System.Tags", StringComparison.OrdinalIgnoreCase))
                    continue;
                cells.TryAdd(refName, RenderCell.String(value));
            }
        }

        return new RenderNode.Record("workItem", cells);
    }

    private static string GetTags(Domain.Aggregates.WorkItem item)
    {
        item.Fields.TryGetValue("System.Tags", out var tags);
        return tags ?? string.Empty;
    }

    private static RenderNode.Section BuildIdSection(IEnumerable<int> ids)
    {
        var children = new List<RenderNode>();
        foreach (var id in ids)
        {
            children.Add(new RenderNode.Record(null, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["id"] = RenderCell.Integer(id),
            }));
        }
        return new RenderNode.Section(null, children);
    }

    private static RenderNode.Section BuildSectionsNode(WorkspaceSections sections)
    {
        var children = new List<RenderNode>(sections.Sections.Count);
        foreach (var section in sections.Sections)
        {
            var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["modeName"] = RenderCell.String(section.ModeName),
                ["itemCount"] = RenderCell.Integer(section.Items.Count),
            };
            children.Add(new RenderNode.Record("workspaceSection", cells));
        }
        return new RenderNode.Section(null, children);
    }

    private static RenderNode.Section BuildTrackedItemsSection(IReadOnlyList<TrackedItem> tracked)
    {
        var children = new List<RenderNode>(tracked.Count);
        foreach (var t in tracked)
        {
            children.Add(new RenderNode.Record("trackedItem", new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["workItemId"] = RenderCell.Integer(t.WorkItemId),
                ["mode"] = RenderCell.String(t.Mode.ToString()),
                ["trackedAt"] = RenderCell.String(t.TrackedAt.ToString("O", CultureInfo.InvariantCulture)),
            }));
        }
        return new RenderNode.Section(null, children);
    }
}