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
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig workspace [show]</c>, <c>twig show</c>, <c>twig ws</c>:
/// displays the current workspace including active context, sprint items, and seeds
/// with stale seed warnings. The ordinary current Bench presentation renders as a
/// tree by default; <c>--view table</c> keeps the current Bench as a table, and the
/// legacy <c>--tree</c> path remains the full-backlog hierarchy view.
/// When <c>--all</c> is specified (or via <c>twig sprint</c>), shows all team items
/// grouped by assignee instead of filtering to the current user.
/// </summary>
/// <remarks>
/// Partially migrated to the AB#3301 <see cref="RendererFactory"/>/<see cref="IRenderer"/>
/// seam: <c>json</c>, <c>minimal</c>, and <c>ids</c> output formats now project
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
    RendererFactory? rendererFactory = null,
    CurrentBenchResolver? currentBench = null,
    BenchEvaluator? benchEvaluator = null,
    IAuthenticationProvider? authenticationProvider = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    internal Func<CancellationToken, Task<ResolvedConnectionBinding>>? ResolveBrowserBindingAsync { get; init; }

    private enum WorkspaceViewMode
    {
        Auto,
        Table,
        Tree,
    }

    private static bool TryResolveWorkspaceViewMode(
        string? view,
        bool all,
        bool sprintLayout,
        bool tree,
        bool flat,
        out WorkspaceViewMode mode,
        out string? error)
    {
        mode = WorkspaceViewMode.Auto;
        error = null;

        if (tree && flat)
        {
            error = "error: --tree and --flat are mutually exclusive.";
            return false;
        }

        if (view is null)
            return true;

        if (all || sprintLayout || tree || flat)
        {
            error = "error: --view cannot be combined with --tree, --flat, --all, or sprint layout.";
            return false;
        }

        switch (view.Trim().ToLowerInvariant())
        {
            case "table":
                mode = WorkspaceViewMode.Table;
                return true;
            case "tree":
                mode = WorkspaceViewMode.Tree;
                return true;
            default:
                error = "error: --view must be 'table' or 'tree'.";
                return false;
        }
    }

    /// <summary>
    /// Resolves the authenticated bound principal (ADO #1106, Spec #1103) for self-scoped read
    /// paths. A connection whose identity carries no canonical <c>uniqueName</c> is refused with
    /// an actionable error — self views never widen to the whole team and never silently fall
    /// back to the display rendering. The error text matches
    /// <see cref="DefaultBenchSelectors.MissingBoundIdentityMessage"/> so the surface report
    /// stays identical whether the gap surfaces here or downstream.
    /// </summary>
    private async Task<(string? Principal, string? Error)> ResolveSelfPrincipalAsync(CancellationToken ct)
    {
        var (_, uniqueName) = await iterationService.GetAuthenticatedUserIdentityAsync(ct);
        return string.IsNullOrWhiteSpace(uniqueName)
            ? (null, DefaultBenchSelectors.MissingBoundIdentityMessage)
            : (uniqueName, null);
    }

    public async Task<int> ExecuteAsync(string outputFormat = OutputFormatterFactory.DefaultFormat, bool all = false, bool noLive = false, bool refresh = false, CancellationToken ct = default, bool sprintLayout = false, bool flat = false, bool tree = false, string? view = null, bool includeBrowser = false, string? expectBinding = null, string? expectIdentity = null)
    {
        if (!TryResolveWorkspaceViewMode(view, all, sprintLayout, tree, flat, out var viewMode, out var viewError))
        {
            Console.Error.WriteLine(viewError);
            return 1;
        }

        if (includeBrowser && (NormalizeOutputFormat(outputFormat) != "json"
            || viewMode != WorkspaceViewMode.Tree || all || sprintLayout || tree || flat))
        {
            Console.Error.WriteLine(ctx.FormatterFactory.GetFormatter(outputFormat).FormatError(
                "--include-browser requires --view tree -o json for the current Bench."));
            return 2;
        }
        if (!includeBrowser && (expectBinding is not null || expectIdentity is not null))
        {
            Console.Error.WriteLine(ctx.FormatterFactory.GetFormatter(outputFormat).FormatError(
                "Expected browser origin guards require --include-browser."));
            return 2;
        }

        IDisposable? browserAdmission;
        ResolvedConnectionBinding? browserBinding = null;
        try
        {
            browserAdmission = includeBrowser && authenticationProvider is not null
                ? await ConnectionOperationAdmission.AcquireAsync(authenticationProvider, ct) : null;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ctx.FormatterFactory.GetFormatter(outputFormat).FormatError(ex.Message));
            return 1;
        }
        using var admission = browserAdmission;
        if (includeBrowser)
        {
            if (ResolveBrowserBindingAsync is null || currentBench is null || benchEvaluator is null)
            {
                Console.Error.WriteLine(ctx.FormatterFactory.GetFormatter(outputFormat).FormatError(
                    "Semantic Bench browsing is unavailable; use a native Twig companion with connection and Bench services."));
                return 1;
            }
            try
            {
                browserBinding = await ResolveBrowserBindingAsync(ct);
                BrowserOriginGuard.EnsureExpected(browserBinding, expectBinding, expectIdentity);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ctx.FormatterFactory.GetFormatter(outputFormat).FormatError(ex.Message));
                return 1;
            }
        }

        // Resolve the authenticated bound principal up front for every self-scoped path (ADO #1106).
        // --all / sprint-layout explicitly opt out of the self filter, so the lookup is skipped and
        // a connection without a canonical identity does NOT block the team view. Self consumers
        // refuse rather than fall back to display or widen to the whole team.
        string? selfPrincipal = null;
        if (!all && !sprintLayout)
        {
            var (principal, error) = await ResolveSelfPrincipalAsync(ct);
            if (error is not null)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            selfPrincipal = principal;
        }

        if (tree)
            return await ExecuteTreeModeAsync(outputFormat, all, noLive, refresh, selfPrincipal, ct);

        var (fmt, renderer) = ctx.Resolve(outputFormat, noLive);
        ProcessConfigurationData? processConfig = renderer is SpectreRenderer || viewMode == WorkspaceViewMode.Tree
            ? await processTypeStore.GetProcessConfigurationDataAsync()
            : null;

        if (renderer is not null && !all && !sprintLayout)
        {
            // NOTE: The async Spectre rendering path is gated by `!all`, so `isTeamView`
            // passed to RenderWorkspaceAsync is always false at runtime. The team/sprint
            // view (--all) always falls through to ExecuteSyncAsync. The Spectre team-view
            // table and assignee column handling is reserved for a future async team-view path.
            Domain.Aggregates.WorkItem? contextItem = null;
            IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems = Array.Empty<Domain.Aggregates.WorkItem>();
            IReadOnlyList<Domain.Aggregates.WorkItem> manualItems = Array.Empty<Domain.Aggregates.WorkItem>();
            IReadOnlyList<Domain.Aggregates.WorkItem> seeds = Array.Empty<Domain.Aggregates.WorkItem>();

            // Load tracking overlay (tracked items + excluded IDs)
            var trackedItems = await trackingService.GetTrackedItemsAsync(ct);
            var excludedIds = await trackingService.GetExcludedIdsAsync(ct);

            var useTreeRendering = viewMode != WorkspaceViewMode.Table && !flat;

            // Wire working level and tree rendering into SpectreRenderer
            if (renderer is SpectreRenderer spectreRenderer)
            {
                spectreRenderer.UseTreeRendering = useTreeRendering;
                spectreRenderer.TreeDepthUp = ctx.Config.Display.TreeDepthUp;
                spectreRenderer.TreeDepthDown = ctx.Config.Display.TreeDepthDown;
                spectreRenderer.TreeDepthSideways = ctx.Config.Display.TreeDepthSideways;
                if (processConfig is not null)
                {
                    spectreRenderer.TypeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
                    spectreRenderer.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;
                }
                else
                {
                    spectreRenderer.TypeLevelMap = null;
                    spectreRenderer.WorkingLevelTypeName = null;
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

                // Stage 2: evaluate the current Bench against the local cache. Query selectors
                // supply the Sprint origin; pin selectors supply the Manual origin. Membership is
                // a union, so remove query matches from Manual before presentation.
                var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
                IReadOnlyList<IterationPath> benchIterations = resolvedIterations;
                if (benchIterations.Count == 0)
                    benchIterations = [await iterationService.GetCurrentIterationAsync(ct)];

                var benchView = await workingSetService.ComputeAsync(benchIterations, ct);
                sprintItems = await LoadQueryMatchesInOrderAsync(
                    benchView.SprintItemIds, benchIterations, selfPrincipal, ct);
                var sprintIds = new HashSet<int>(benchView.SprintItemIds);
                manualItems = await LoadItemsInOrderAsync(
                    benchView.TrackedItemIds.Where(id => !sprintIds.Contains(id)).ToArray(), ct);

                var treeRoots = await BuildTreeRootsAsync(sprintItems, ct);
                var sections = WorkspaceSections.Build(
                    sprintItems, manualItems: manualItems, excludedIds: excludedIds, treeRoots: treeRoots);
                if (useTreeRendering)
                    sections = await AddSectionHierarchiesAsync(sections, ct);
                yield return new SprintItemsLoaded(sprintItems, sections);

                // Stage 3: Seeds
                seeds = await workItemRepo.GetSeedsAsync(ct);
                yield return new SeedsLoaded(seeds);

                // Stage 4: Check cache freshness for stale-while-revalidate (EPIC-006)
                // Wayfinder 0004 §3: the revalidate pass is opt-in via --refresh.
                if (refresh)
                {
                    var lastRefreshedRaw = await contextStore.GetValueAsync("last_refreshed_at", ct);
                    if (IsCacheStale(lastRefreshedRaw, ctx.Config.Display.CacheStaleMinutes))
                    {
                        yield return new RefreshStarted();

                        // Cannot yield inside try/catch in C# iterators — collect results first.
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSprintItems = null;
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedManualItems = null;
                        IReadOnlyList<Domain.Aggregates.WorkItem>? refreshedSeeds = null;
                        bool refreshFailed = false;

                        try
                        {
                            // Re-evaluate the current Bench against the refreshed local cache.
                            var freshIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
                            IReadOnlyList<IterationPath> freshBenchIterations = freshIterations;
                            if (freshBenchIterations.Count == 0)
                                freshBenchIterations = [await iterationService.GetCurrentIterationAsync(ct)];

                            var freshBenchView = await workingSetService.ComputeAsync(freshBenchIterations, ct);
                            refreshedSprintItems = await LoadQueryMatchesInOrderAsync(
                                freshBenchView.SprintItemIds, freshBenchIterations, selfPrincipal, ct);
                            var refreshedSprintIds = new HashSet<int>(freshBenchView.SprintItemIds);
                            refreshedManualItems = await LoadItemsInOrderAsync(
                                freshBenchView.TrackedItemIds.Where(id => !refreshedSprintIds.Contains(id)).ToArray(), ct);

                            // Re-fetch seeds
                            refreshedSeeds = await workItemRepo.GetSeedsAsync(ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Re-fetch failed (network timeout, auth failure, etc.) —
                            // fall back to original data so the renderer shows stale rows rather than an empty table.
                            refreshFailed = true;
                        }

                        if (!refreshFailed && refreshedSprintItems is not null && refreshedManualItems is not null && refreshedSeeds is not null)
                        {
                            // Update closure variables so hint computation uses refreshed data
                            sprintItems = refreshedSprintItems;
                            manualItems = refreshedManualItems;
                            seeds = refreshedSeeds;

                            // Update freshness timestamp (best-effort — persistence failure must not discard fetched data)
                            try { await contextStore.SetValueAsync("last_refreshed_at", DateTimeOffset.UtcNow.ToString("O"), ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort; data display is unaffected */ }
                        }

                        // Yield data rows (refreshed on success, original on failure)
                        var refreshTreeRoots = await BuildTreeRootsAsync(sprintItems, ct);
                        var refreshedSections = WorkspaceSections.Build(
                            sprintItems, manualItems: manualItems, excludedIds: excludedIds, treeRoots: refreshTreeRoots);
                        if (useTreeRendering)
                            refreshedSections = await AddSectionHierarchiesAsync(refreshedSections, ct);
                        yield return new SprintItemsLoaded(sprintItems, refreshedSections);
                        yield return new SeedsLoaded(seeds);
                        yield return new RefreshCompleted();
                    }
                }
            }

            await renderer.RenderWorkspaceAsync(StreamWorkspaceData(ct), ctx.Config.Seed.StaleDays, all, ct, dynamicColumns, ctx.Config.Display.CacheStaleMinutes);

            // Build Workspace from closure-populated variables for hint computation
            var workspace = Workspace.Build(contextItem, sprintItems, seeds,
                sections: WorkspaceSections.Build(
                    sprintItems,
                    manualItems: manualItems,
                    excludedIds: excludedIds),
                trackedItems: trackedItems,
                excludedIds: excludedIds);

            var hints = ctx.HintEngine.GetHints("workspace",
                workspace: workspace,
                outputFormat: outputFormat);
            renderer.RenderHints(hints);

            return 0;
        }

        // Sync path — original implementation (JSON, minimal, --no-live, --all, sprint, piped output)
        return await ExecuteSyncAsync(fmt, outputFormat, all, selfPrincipal, refresh, sprintLayout, flat, viewMode, browserBinding, ct);
    }

    /// <summary>
    /// Full-backlog tree mode: renders each sprint item as an independent tree root
    /// expanded to the configured depth. Delegates to <see cref="TreeRenderingService"/>
    /// for per-item rendering so all output formats (human, json, minimal) work consistently.
    /// </summary>
    private async Task<int> ExecuteTreeModeAsync(string outputFormat, bool all, bool noLive, bool refresh, string? selfPrincipal, CancellationToken ct)
    {
        if (treeRenderingService is null)
        {
            Console.Error.WriteLine("error: Tree rendering is not available.");
            return 1;
        }

        // Gather sprint items using the same logic as the sync path. The sprint rule is bound to
        // the authenticated canonical principal (ADO #1106) when self-scoped; --all explicitly
        // bypasses the filter.
        var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints, ct);
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems;

        if (resolvedIterations.Count > 0)
        {
            sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                resolvedIterations, selfPrincipal, allUsers: all, ct);
        }
        else
        {
            var iteration = await iterationService.GetCurrentIterationAsync(ct);
            sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                [iteration], selfPrincipal, allUsers: all, ct);
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
            var itemRefresh = refresh && i == 0;
            var result = await treeRenderingService.RenderTreeAsync(
                sprintItems[i].Id, outputFormat, depth: null, noLive, itemRefresh, ct);
            if (result != 0) return result;
        }

        return 0;
    }

    private async Task<int> ExecuteSyncAsync(IOutputFormatter fmt, string outputFormat, bool all, string? selfPrincipal, bool refresh = false, bool sprintLayout = false, bool flat = false, WorkspaceViewMode viewMode = WorkspaceViewMode.Auto, ResolvedConnectionBinding? browserBinding = null, CancellationToken ct = default)
    {
        // Sync-first for machine formats: ensure consumers get fresh data.
        // The human (TTY) path handles sync via the live streaming path above.
        var isMachineFormat = IsMachineFormat(outputFormat);
        if (isMachineFormat && refresh && syncCoordinatorFactory is not null)
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

        // Normal workspace is a projection of the current Bench. Team/sprint layouts remain
        // explicit sprint views and therefore retain the direct iteration query.
        var resolvedIterations = await ResolveSprintIterationsAsync(ctx.Config.Workspace.Sprints);
        IReadOnlyList<Domain.Aggregates.WorkItem> sprintItems;
        IReadOnlyList<Domain.Aggregates.WorkItem> manualItems = Array.Empty<Domain.Aggregates.WorkItem>();
        WorkingSet? benchView = null;
        Bench? observedBench = null;
        BenchMembership? observedMembership = null;

        if (!all && !sprintLayout)
        {
            IReadOnlyList<IterationPath> benchIterations = resolvedIterations;
            if (benchIterations.Count == 0)
                benchIterations = [await iterationService.GetCurrentIterationAsync()];

            if (browserBinding is not null)
            {
                observedBench = await currentBench!.ResolveAsync(ct);
                observedMembership = await benchEvaluator!.EvaluateAsync(observedBench, benchIterations, ct);
                benchView = new WorkingSet
                {
                    SprintItemIds = observedMembership.QueryMatches.Select(item => item.Id).ToArray(),
                    TrackedItemIds = observedMembership.PinnedIds,
                    SeedIds = observedMembership.SeedIds,
                    DirtyItemIds = observedMembership.DirtyItemIds,
                    IterationPaths = observedMembership.IterationPaths,
                };
                sprintItems = observedMembership.QueryMatches;
            }
            else
            {
                benchView = await workingSetService.ComputeAsync(benchIterations);
                sprintItems = await LoadQueryMatchesInOrderAsync(
                    benchView.SprintItemIds, benchIterations, selfPrincipal);
            }
            var sprintIds = new HashSet<int>(benchView.SprintItemIds);
            manualItems = await LoadItemsInOrderAsync(
                benchView.TrackedItemIds.Where(id => !sprintIds.Contains(id)).ToArray());
        }
        else
        {
            // Explicit team / sprint-layout path: ADO #1106 keeps this strictly off the self
            // filter, so a connection without a bound principal does NOT block --all.
            if (resolvedIterations.Count > 0)
            {
                sprintItems = await GetSprintItemsFromResolvedIterationsAsync(
                    resolvedIterations, canonicalPrincipal: null, allUsers: true);
            }
            else
            {
                var iteration = await iterationService.GetCurrentIterationAsync();
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

        // Build hierarchy when sprint items exist. Process metadata enriches the
        // projection with working-level and virtual-group semantics, but parent IDs
        // alone still provide a real tree for the current Bench's default view.
        SprintHierarchy? hierarchy = null;
        IReadOnlyList<SprintHierarchyNode>? treeRoots = null;
        IReadOnlyDictionary<string, int>? typeLevelMap = null;
        ProcessConfigurationData? processConfig = null;
        var useTreeRendering = viewMode != WorkspaceViewMode.Table && !flat && !all && !sprintLayout;
        if (useTreeRendering || sprintItems.Count > 0)
            processConfig = await processTypeStore.GetProcessConfigurationDataAsync();
        if (processConfig is not null)
            typeLevelMap = BacklogHierarchyService.GetTypeLevelMap(processConfig);

        if (sprintItems.Count > 0 && browserBinding is null)
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

            IReadOnlyList<string>? ceilingTypeNames = useTreeRendering
                ? ComputeFallbackCeilingTypes(parentLookup)
                : null;
            if (processConfig is not null)
            {
                var typeNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in sprintItems)
                    typeNameSet.Add(item.Type.Value);

                ceilingTypeNames = CeilingComputer.Compute(new List<string>(typeNameSet), processConfig)
                    ?? ceilingTypeNames;
                typeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
            }

            if (processConfig is not null || useTreeRendering)
                hierarchy = sprintHierarchyBuilder.Build(sprintItems, parentLookup, ceilingTypeNames, typeLevelMap);
            if (hierarchy is not null)
            {
                // Extract tree roots from hierarchy for tree-based rendering
                var roots = new List<SprintHierarchyNode>();
                foreach (var group in hierarchy.AssigneeGroups.Values)
                    foreach (var node in group)
                        roots.Add(node);
                treeRoots = roots.Count > 0 ? roots : null;
            }
        }

        // Wire tree rendering config into HumanOutputFormatter (mirrors SpectreRenderer wiring)
        if (fmt is HumanOutputFormatter humanFmt)
        {
            if (typeLevelMap is not null)
            {
                humanFmt.TypeLevelMap = typeLevelMap;
                humanFmt.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;
            }
            else
            {
                humanFmt.TypeLevelMap = null;
                humanFmt.WorkingLevelTypeName = null;
            }

            humanFmt.UseTreeRendering = useTreeRendering;
            humanFmt.TreeDepthUp = ctx.Config.Display.TreeDepthUp;
            humanFmt.TreeDepthDown = ctx.Config.Display.TreeDepthDown;
            humanFmt.TreeDepthSideways = ctx.Config.Display.TreeDepthSideways;
        }

        var sections = WorkspaceSections.Build(
            sprintItems, manualItems: manualItems, excludedIds: excludedIds, treeRoots: treeRoots);
        if (useTreeRendering && browserBinding is null)
            sections = await AddSectionHierarchiesAsync(sections);
        var workspace = Workspace.Build(contextItem, sprintItems, seeds, hierarchy,
            sections: sections, trackedItems: trackedItems, excludedIds: excludedIds);

        var browser = browserBinding is not null
            ? await BuildBrowserDocumentAsync(workspace, observedBench!, observedMembership!, browserBinding, typeLevelMap, ct)
            : null;

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
            RenderWorkspaceAsTree(workspace, outputFormat, useSprintLayout: all || sprintLayout, ctx.Config.Seed.StaleDays, dynamicColumns, browser);
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
            var dirtyWorkingSet = benchView ?? await workingSetService.ComputeAsync(orphanIterations);
            if (dirtyWorkingSet.DirtyItemIds.Count > 0)
            {
                var sprintItemIds = new HashSet<int>(sprintItems.Select(s => s.Id));
                var seedIds = new HashSet<int>(seeds.Select(s => s.Id));
                var orphanIds = new List<int>();
                foreach (var dirtyId in dirtyWorkingSet.DirtyItemIds)
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
    /// Materializes query-origin matches from the same local iteration rows the selector evaluated,
    /// then orders them by the evaluator's deterministic ID list. The Bench remains authoritative:
    /// rows not selected by its query selectors are filtered out, including every sprint row on a
    /// pin-only Bench.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Aggregates.WorkItem>> LoadQueryMatchesInOrderAsync(
        IReadOnlyList<int> selectedIds,
        IReadOnlyList<IterationPath> iterations,
        string? canonicalPrincipal,
        CancellationToken ct = default)
    {
        if (selectedIds.Count == 0)
            return Array.Empty<Domain.Aggregates.WorkItem>();

        // Self-scoped Bench query: load unfiltered iteration rows, then narrow in-memory by the
        // bound canonical identity (ADO #1106). Loading unfiltered matches the Bench evaluator's
        // own path, so the intersection with the evaluator's deterministic ID list reflects what
        // the Bench actually said — never a repo-level display filter the evaluator would reject.
        var candidates = await GetSprintItemsFromResolvedIterationsAsync(
            iterations, canonicalPrincipal, allUsers: false, ct);
        var byId = candidates.ToDictionary(item => item.Id);
        var ordered = new List<Domain.Aggregates.WorkItem>(selectedIds.Count);
        foreach (var id in selectedIds)
        {
            if (byId.TryGetValue(id, out var item))
                ordered.Add(item);
        }
        return ordered;
    }

    /// <summary>
    /// Loads cached work items in the evaluator's deterministic ID order. Repository batch
    /// ordering is an implementation detail and must not leak into Bench presentation.
    /// Missing IDs are stale selectors and are omitted; reads remain cache-only.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Aggregates.WorkItem>> LoadItemsInOrderAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return Array.Empty<Domain.Aggregates.WorkItem>();

        var loaded = await workItemRepo.GetByIdsAsync(ids, ct);
        var byId = loaded.ToDictionary(item => item.Id);
        var ordered = new List<Domain.Aggregates.WorkItem>(ids.Count);
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var item))
                ordered.Add(item);
        }
        return ordered;
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
    /// Derives root type ceilings when process metadata is unavailable. Parent
    /// chains still carry enough information to preserve hierarchy; the missing
    /// type map only disables virtual groups and working-level enrichment.
    /// </summary>
    private static IReadOnlyList<string>? ComputeFallbackCeilingTypes(
        IReadOnlyDictionary<int, Domain.Aggregates.WorkItem> parentLookup)
    {
        if (parentLookup.Count == 0)
            return null;

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parentLookup.Values)
        {
            if (!item.ParentId.HasValue || !parentLookup.ContainsKey(item.ParentId.Value))
                roots.Add(item.Type.Value);
        }

        return roots.Count == 0 ? null : roots.ToArray();
    }

    private async Task<WorkspaceSections> AddSectionHierarchiesAsync(
        WorkspaceSections sections, CancellationToken ct = default)
    {
        var enriched = new List<WorkspaceSection>(sections.Sections.Count);
        foreach (var section in sections.Sections)
        {
            var roots = section.TreeRoots ?? await BuildTreeRootsAsync(section.Items, ct);
            enriched.Add(section with { TreeRoots = roots is null ? null : MergeBenchRoots(roots) });
        }
        return WorkspaceSections.BuildWithTreeRoots(enriched, sections.ExcludedItemIds);
    }

    // The sprint builder groups by assignee. A bench tree has one identity per
    // item, so shared ancestors must be merged when those groups are flattened.
    private static IReadOnlyList<SprintHierarchyNode> MergeBenchRoots(IEnumerable<SprintHierarchyNode> roots)
    {
        var result = new List<SprintHierarchyNode>();
        foreach (var group in roots.GroupBy(node =>
            (node.IsVirtualGroup, Id: node.IsVirtualGroup ? 0 : node.Item.Id, node.GroupLabel, node.BacklogLevel)))
        {
            var node = group.First();
            if (group.Skip(1).Any())
            {
                var children = MergeBenchRoots(group.SelectMany(part => part.Children));
                node.Children.Clear();
                node.Children.AddRange(children);
            }
            result.Add(node);
        }
        return result;
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
        IReadOnlyList<string>? ceilingTypeNames = ComputeFallbackCeilingTypes(parentLookup);
        IReadOnlyDictionary<string, int>? typeLevelMap = null;
        if (processConfig is not null)
        {
            var typeNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in sprintItems)
                typeNameSet.Add(item.Type.Value);

            ceilingTypeNames = CeilingComputer.Compute(new List<string>(typeNameSet), processConfig)
                ?? ceilingTypeNames;
            typeLevelMap = Domain.Services.Workspace.BacklogHierarchyService.GetTypeLevelMap(processConfig);
        }

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
    /// When <paramref name="allUsers"/> is <c>false</c> and <paramref name="canonicalPrincipal"/>
    /// is non-empty, items are narrowed in-memory by the bound canonical identity (ADO #1106):
    /// the row's <see cref="Domain.Aggregates.WorkItem.AssignedToUniqueName"/> is matched first,
    /// then — only when the row carries no canonical column — the authored
    /// <see cref="Domain.Aggregates.WorkItem.AssignedTo"/> string is matched exactly. The filter
    /// is NEVER widened to the display label because two accounts can share a display and that is
    /// the merge canonical scoping exists to prevent.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Aggregates.WorkItem>> GetSprintItemsFromResolvedIterationsAsync(
        IReadOnlyList<IterationPath> resolvedIterations,
        string? canonicalPrincipal,
        bool allUsers,
        CancellationToken ct = default)
    {
        var seenIds = new HashSet<int>();
        var result = new List<Domain.Aggregates.WorkItem>();
        var narrowToSelf = !allUsers && !string.IsNullOrWhiteSpace(canonicalPrincipal);

        foreach (var path in resolvedIterations)
        {
            // Load unfiltered: a repo-level filter can only key off the display rendering, and
            // narrowing by canonical identity has to happen on the loaded row.
            var items = await workItemRepo.GetByIterationAsync(path, ct);

            foreach (var item in items)
            {
                if (narrowToSelf)
                {
                    if (!item.IsAssignedToIdentity(canonicalPrincipal))
                        continue;
                }
                if (seenIds.Add(item.Id))
                    result.Add(item);
            }
        }

        return result;
    }


    private async Task<RenderNode.Document> BuildBrowserDocumentAsync(
        Workspace workspace, Bench bench, BenchMembership membership, ResolvedConnectionBinding binding,
        IReadOnlyDictionary<string, int>? typeLevelMap, CancellationToken ct)
    {
        // One native hierarchy for every visible member: shared ancestors are merged across
        // selector origins and assignees, while unpublished and owed work cannot disappear.
        var items = new List<WorkItem>();
        var seen = new HashSet<int>();
        var excluded = new HashSet<int>(workspace.ExcludedIds);
        foreach (var section in workspace.Sections!.Sections)
            foreach (var item in section.Items)
                if (seen.Add(item.Id)) items.Add(item);
        foreach (var item in await LoadItemsInOrderAsync(membership.DirtyItemIds.Order().ToArray(), ct))
            if (seen.Add(item.Id)) items.Add(item);
        foreach (var seed in workspace.Seeds)
            if (seen.Add(seed.Id)) items.Add(seed);
        if (workspace.ContextItem is { } context && !excluded.Contains(context.Id) && seen.Add(context.Id))
            items.Add(context);

        var roots = await BuildTreeRootsAsync(items, ct) ?? [];
        roots = MergeBenchRoots(roots);
        roots = WorkingLevelResolver.PruneAncestors(roots, ctx.Config.Workspace.WorkingLevel,
            typeLevelMap, ctx.Config.Display.TreeDepthUp);
        var formatter = (HumanOutputFormatter)ctx.FormatterFactory.GetFormatter("human");
        formatter.TypeLevelMap = typeLevelMap;
        formatter.WorkingLevelTypeName = ctx.Config.Workspace.WorkingLevel;
        var memberIds = membership.SelectedIds;
        var emitted = new HashSet<int>();
        var singlePins = new HashSet<int>();
        var treePins = new HashSet<int>();
        foreach (var selector in bench.Selectors)
        {
            if (selector.Kind == Domain.Enums.SelectorKind.Item) singlePins.Add(selector.AsWorkItemId());
            else if (selector.Kind == Domain.Enums.SelectorKind.Subtree) treePins.Add(selector.AsWorkItemId());
        }

        List<RenderCell> Project(IEnumerable<SprintHierarchyNode> nodes, int depth)
        {
            var result = new List<RenderCell>();
            foreach (var node in nodes)
            {
                if (node.IsVirtualGroup)
                {
                    result.AddRange(Project(node.Children, depth));
                    continue;
                }
                var item = node.Item;
                if (!emitted.Add(item.Id)) continue;
                var pins = new List<RenderCell>(2);
                if (singlePins.Contains(item.Id)) pins.Add(RenderCell.String("single"));
                if (treePins.Contains(item.Id)) pins.Add(RenderCell.String("tree"));
                membership.OwningSubtreeIds.TryGetValue(item.Id, out var owners);
                var summary = item.IsSeed ? "seed"
                    : membership.DirtyItemIds.Contains(item.Id) ? "pending"
                    : treePins.Contains(item.Id) || owners is { Count: > 0 } ? "subtree"
                    : memberIds.Contains(item.Id) ? "bench member" : "ancestor context";
                var children = depth < ctx.Config.Display.TreeDepthDown ? Project(node.Children, depth + 1) : [];
                var fields = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["key"] = RenderCell.String("item:" + item.Id.ToString(CultureInfo.InvariantCulture)),
                    ["id"] = RenderCell.Integer(item.Id),
                    ["title"] = RenderCell.String(item.Title),
                    ["type"] = RenderCell.String(item.Type.Value),
                    ["state"] = RenderCell.String(item.State),
                    ["label"] = RenderCell.String(formatter.FormatBrowserNodeLabel(workspace, node,
                        singlePins.Contains(item.Id) || treePins.Contains(item.Id))),
                    ["isSeed"] = RenderCell.Boolean(item.IsSeed),
                    ["pins"] = new RenderCell(string.Empty, new RenderValue.Array(pins)),
                    ["owningSubtreeIds"] = new RenderCell(string.Empty, new RenderValue.Array(
                        owners is null ? [] : owners.Select(id => RenderCell.Integer(id)).ToArray())),
                    ["membership"] = RenderCell.String(summary),
                    ["children"] = new RenderCell(string.Empty, new RenderValue.Array(children)),
                };
                result.Add(new RenderCell(string.Empty, new RenderValue.Object(fields)));
            }
            return result;
        }

        var projectedRoots = Project(roots, 0);
        // Depth and working-level presentation may hide ordinary descendants,
        // never a seed or outstanding edit. Promote any omitted guarded row.
        var omittedGuarded = items.Where(item => !emitted.Contains(item.Id)
            && (item.IsSeed || membership.DirtyItemIds.Contains(item.Id)))
            .Select(item => new SprintHierarchyNode(item, memberIds.Contains(item.Id)));
        projectedRoots.AddRange(Project(omittedGuarded, 0));

        return new RenderNode.Document(null,
        [
            new("version", new RenderNode.KeyValue("version", RenderCell.Integer(1))),
            new("benchId", new RenderNode.KeyValue("benchId", RenderCell.String(bench.Id.ToString(CultureInfo.InvariantCulture)))),
            new("benchName", new RenderNode.KeyValue("benchName", RenderCell.String(bench.Name))),
            new("bindingId", new RenderNode.KeyValue("bindingId", RenderCell.String(binding.Binding.BindingId))),
            new("identityId", new RenderNode.KeyValue("identityId", RenderCell.String(binding.Identity.IdentityId))),
            new("worktreeRoot", new RenderNode.KeyValue("worktreeRoot", RenderCell.String(binding.WorktreeRoot))),
            new("roots", new RenderNode.KeyValue("roots", new RenderCell(string.Empty, new RenderValue.Array(projectedRoots)))),
        ]);
    }
    // ── RenderTree projection for machine output formats (json/minimal/ids) ────────
    // The human path keeps using HumanOutputFormatter to preserve rich-format behavior
    // (active marker, dirty/stale glyphs, tree layout). These helpers produce the
    // structural projection consumed by the JSON / minimal / ids renderers.

    private void RenderWorkspaceAsTree(
        Workspace workspace,
        string outputFormat,
        bool useSprintLayout,
        int staleDays,
        IReadOnlyList<ColumnSpec>? dynamicColumns,
        RenderNode.Document? browser = null)
    {
        var doc = useSprintLayout
            ? BuildSprintViewDocument(workspace, dynamicColumns)
            : BuildWorkspaceDocument(workspace, staleDays, dynamicColumns);
        if (browser is not null)
            doc = doc with { Fields = doc.Fields.Append(new DocumentField("browser", browser)).ToArray() };

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

/// <summary>Expected origins are preconditions, never authentication selectors or fallback identities.</summary>
internal static class BrowserOriginGuard
{
    internal static void EnsureExpected(ResolvedConnectionBinding binding, string? expectBinding, string? expectIdentity)
    {
        if ((expectBinding is not null && !string.Equals(expectBinding, binding.Binding.BindingId, StringComparison.Ordinal))
            || (expectIdentity is not null && !string.Equals(expectIdentity, binding.Identity.IdentityId, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "The browser connection or identity changed. Reconnect before retrying; no Bench changes were made.");
    }
}