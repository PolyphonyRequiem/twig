using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for read-only queries: twig_tree, twig_workspace.
/// Resolves per-workspace services via <see cref="ConnectionResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class ReadTools(ConnectionResolver resolver, NavigationTools navigationTools)
{
    [McpServerTool(Name = "twig_tree"), Description("Display work item hierarchy as a tree. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Tree(
        [Description("Work item ID to display. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Max child depth to display")] int? depth = null,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await WorkItemResolver.ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        return await navigationTools.Show(item.Id, tree: true, depth: depth, workspace: workspace, verbose: verbose, ct: ct);
    }

    [McpServerTool(Name = "twig_workspace"), Description("Returns the current sprint workspace: active context item, sprint backlog items, and seeds. When tree=true, returns a tree-structured JSON with full backlog hierarchy.")]
    public async Task<CallToolResult> Workspace(
        [Description("Show all team items instead of just the current user")] bool all = false,
        [Description("When true, returns a tree-structured JSON response showing the full backlog hierarchy instead of the flat workspace view")] bool tree = false,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // 1. Context item(nullable — no error if absent)
        var contextId = await ctx.Get<IContextStore>().GetActiveWorkItemIdAsync(ct);
        WorkItem? contextItem = contextId.HasValue
            ? await ctx.Get<IWorkItemRepository>().GetByIdAsync(contextId.Value, ct)
            : null;

        // 2. Sprint items — use configured sprints when available, else fall back to current iteration
        var sprintEntries = ctx.Config.Workspace.Sprints;
        IReadOnlyList<WorkItem> sprintItems;

        if (sprintEntries is { Count: > 0 })
        {
            // Resolve configured sprint expressions via SprintIterationResolver
            var expressions = new List<IterationExpression>(sprintEntries.Count);
            foreach (var entry in sprintEntries)
            {
                var parseResult = IterationExpression.Parse(entry.Expression);
                if (parseResult.IsSuccess)
                    expressions.Add(parseResult.Value);
            }

            sprintItems = await ctx.Get<SprintIterationResolver>().GetSprintItemsAsync(
                expressions,
                ctx.Config.User.DisplayName,
                allUsers: all,
                ct);
        }
        else
        {
            // No configured sprints — fall back to current iteration
            var iteration = await ctx.Get<IIterationService>().GetCurrentIterationAsync(ct);
            sprintItems = !all && ctx.Config.User.DisplayName is not null
                ? await ctx.Get<IWorkItemRepository>().GetByIterationAndAssigneeAsync(iteration, ctx.Config.User.DisplayName, ct)
                : await ctx.Get<IWorkItemRepository>().GetByIterationAsync(iteration, ct);
        }

        // 3. Seeds
        var seeds = await ctx.Get<IWorkItemRepository>().GetSeedsAsync(ct);

        // 4. Tracked items and exclusions
        var trackedItems = ctx.Get<ITrackingRepository>() is not null
            ? await ctx.Get<ITrackingRepository>().GetAllTrackedAsync(ct)
            : Array.Empty<TrackedItem>();
        var excludedItems = ctx.Get<ITrackingRepository>() is not null
            ? await ctx.Get<ITrackingRepository>().GetAllExcludedAsync(ct)
            : Array.Empty<ExcludedItem>();
        var excludedIds = excludedItems.Select(e => e.WorkItemId).ToList();

        // 5. Build workspace
        var ws = Domain.ReadModels.Workspace.Build(contextItem, sprintItems, seeds,
            trackedItems: trackedItems, excludedIds: excludedIds);

        // 6. Tree mode — build WorkTree per sprint item and return tree-structured JSON
        if (tree)
        {
            var treeResult = await BuildWorkspaceTreeAsync(ctx, ws, excludedItems, ct);
            return await EnvelopeBuilder.WrapAsync(ctx, treeResult, verbose, ct);
        }

        // 7. Format flat result
        var toolResult = McpResultBuilder.FormatWorkspace(ws, ctx.Config.Seed.StaleDays, ctx.Connection.ToString(), excludedItems);
        return await EnvelopeBuilder.WrapAsync(ctx, toolResult, verbose, ct);
    }

    private static async Task<CallToolResult> BuildWorkspaceTreeAsync(
        ConnectionScope ctx,
        Domain.ReadModels.Workspace ws,
        IReadOnlyList<ExcludedItem> excludedItems,
        CancellationToken ct)
    {
        var maxDepth = ctx.Config.Display.TreeDepth;
        var roots = new List<(WorkTree Tree, int TotalChildren)>();

        foreach (var item in ws.SprintItems)
        {
            // Build parent chain
            var parentChain = item.ParentId.HasValue
                ? await ctx.Get<IWorkItemRepository>().GetParentChainAsync(item.ParentId.Value, ct)
                : Array.Empty<WorkItem>();

            var allChildren = await ctx.Get<WorkItemFetcher>().FetchChildrenWithFallbackAsync(item.Id, ct);
            var totalChildCount = allChildren.Count;

            // Recursively fetch descendants up to maxDepth
            var descendantsByParentId = new Dictionary<int, IReadOnlyList<WorkItem>>();
            await WorkTreeFetcher.FetchDescendantsAsync(
                ctx.Get<WorkItemFetcher>().FetchChildrenWithFallbackAsync, allChildren, maxDepth - 1, descendantsByParentId, ct);

            var workTree = WorkTree.Build(item, parentChain, allChildren,
                descendantsByParentId: descendantsByParentId);
            roots.Add((workTree, totalChildCount));
        }

        return McpResultBuilder.FormatWorkspaceTree(roots, ws, ctx.Connection.ToString(), excludedItems);
    }

    [McpServerTool(Name = "twig_refresh"), Description("Pull-only cache refresh from ADO — no pending changes are pushed. When id is omitted, refreshes the full active context (active item, parent chain, children). When id is provided, refreshes only that single work item.")]
    public async Task<CallToolResult> Refresh(
        [Description("Work item ID to refresh. When omitted, refreshes the full active context.")] int? id = null,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int refreshedCount = 0;

        if (id.HasValue)
        {
            // Single-item refresh
            var result = await ctx.Get<SyncCoordinatorFactory>().ReadWrite.SyncItemSetAsync([id.Value], ct);
            refreshedCount = result switch
            {
                Updated u => u.ChangedCount,
                PartiallyUpdated p => p.SavedCount,
                _ => 0
            };
        }
        else
        {
            // Full context refresh — same pull logic as twig_sync's phase 2
            var resolved = await ctx.Get<ActiveItemResolver>().GetActiveItemAsync(ct);
            if (resolved is Found or FetchedFromAdo)
            {
                var item = resolved switch
                {
                    Found f => f.WorkItem,
                    FetchedFromAdo a => a.WorkItem,
                    _ => throw new InvalidOperationException("Unexpected active item result"),
                };

                var idsToSync = new List<int> { item.Id };

                if (item.ParentId.HasValue)
                {
                    var chain = await ctx.Get<IWorkItemRepository>().GetParentChainAsync(item.ParentId.Value, ct);
                    idsToSync.AddRange(chain.Select(p => p.Id));
                }

                var children = await ctx.Get<IWorkItemRepository>().GetChildrenAsync(item.Id, ct);
                idsToSync.AddRange(children.Select(c => c.Id));

                try
                {
                    var result = await ctx.Get<SyncCoordinatorFactory>().ReadWrite.SyncItemSetAsync(
                        idsToSync.Distinct().ToList(), ct);
                    refreshedCount = result switch
                    {
                        Updated u => u.ChangedCount,
                        PartiallyUpdated p => p.SavedCount,
                        _ => 0
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch { /* best-effort */ }
            }
        }

        sw.Stop();

        var stats = await ctx.Get<IWorkItemRepository>().GetCacheStatisticsAsync(ct);
        var lastSyncUtc = stats.NewestSyncUtc?.ToString("o") ?? "";

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("refreshedCount", refreshedCount);
            writer.WriteString("lastSyncUtc", lastSyncUtc);
            writer.WriteNumber("durationMs", sw.ElapsedMilliseconds);
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_history"), Description("Return a work item's revision history: field changes with old/new values and relation adds/removes, each with a timestamp and actor. Read-only. Brief by default; use detail for values.")]
    public async Task<CallToolResult> History(
        [Description("Work item ID.")] int id,
        [Description("Update IDs (e.g. '8,11,14') or 'all' for full field values.")] string? detail = null,
        [Description("ADO field reference names to filter to. Relation events are always kept.")] string? field = null,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (id <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "A positive work item id is required.");

        var parsed = WorkItemHistoryOptionsParser.Parse(detail, field);
        if (!parsed.IsSuccess)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, parsed.Error);

        WorkItemHistory history;
        try
        {
            history = await ctx.Get<IAdoWorkItemService>().FetchHistoryAsync(id, parsed.Value, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Complete-or-error: a failure never degrades to an empty successful timeline.
            // History-specific structured error codes are deferred (#249) — reuse existing ones.
            var code = ex is Twig.Infrastructure.Ado.Exceptions.AdoNotFoundException
                ? McpErrorCode.ItemNotFound
                : McpErrorCode.AdoUnreachable;

            return await EnvelopeBuilder.ErrorAsync(
                code, $"Failed to read history for #{id}: {ex.Message}", ctx, ct);
        }

        // Shares the CLI's AOT-safe writer verbatim, so both surfaces emit an identical document.
        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("workItemId", history.WorkItemId);
            writer.WriteBoolean("complete", history.Complete);
            writer.WriteNumber("eventCount", history.Events.Count);
            writer.WritePropertyName("events");
            using var document = JsonDocument.Parse(WorkItemHistoryJsonWriter.Write(history));
            document.RootElement.GetProperty("events").WriteTo(writer);
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_cache_status"), Description("Report local cache freshness: last sync time, pending change count, tracked item count, oldest item age. No network call — safe for polling.")]
    public async Task<CallToolResult> CacheStatus(
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var stats = await ctx.Get<IWorkItemRepository>().GetCacheStatisticsAsync(ct);
        var pendingChangeCount = await ctx.Get<IPendingChangeStore>().GetTotalPendingChangeCountAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var oldestItemAgeSeconds = stats.OldestSyncUtc.HasValue
            ? (long)Math.Max(0, (now - stats.OldestSyncUtc.Value).TotalSeconds)
            : 0L;

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteString("lastSyncUtc", stats.NewestSyncUtc?.ToString("o") ?? "");
            writer.WriteNumber("pendingChangeCount", pendingChangeCount);
            writer.WriteNumber("trackedItemCount", stats.TrackedItemCount);
            writer.WriteNumber("oldestItemAgeSeconds", oldestItemAgeSeconds);
        }, verbose, ct);
    }
}