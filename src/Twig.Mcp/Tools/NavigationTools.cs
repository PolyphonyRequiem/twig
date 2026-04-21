using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for read-only navigation: twig_show, twig_query, twig_children, twig_parent, twig_sprint.
/// All tools accept explicit IDs or search parameters and do not require or modify the active context.
/// </summary>
[McpServerToolType]
public sealed class NavigationTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_show"), Description("Read a work item by ID without changing the active context")]
    public async Task<CallToolResult> Show(
        [Description("Work item ID to retrieve")] int id,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        var (item, fetchErr) = await FetchWithFallbackAsync(ctx, id, ct);
        if (fetchErr is not null) return fetchErr;

        return McpResultBuilder.FormatWorkItem(item!, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_query"), Description("Search work items with structured filters (type, state, title, assignedTo, etc.)")]
    public async Task<CallToolResult> Query(
        [Description("Free-text search across title and description")] string? searchText = null,
        [Description("Filter by work item type (exact match)")] string? type = null,
        [Description("Filter by state (exact match)")] string? state = null,
        [Description("Filter by title text (CONTAINS)")] string? title = null,
        [Description("Filter by assignee display name (exact match)")] string? assignedTo = null,
        [Description("Filter by area path (UNDER)")] string? areaPath = null,
        [Description("Filter by iteration path (UNDER)")] string? iterationPath = null,
        [Description("Maximum results to return (default: 25)")] int top = 25,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        // Resolve default area paths from config when no explicit area path filter is given
        IReadOnlyList<(string Path, bool IncludeChildren)>? defaultAreaPaths = null;
        if (string.IsNullOrWhiteSpace(areaPath))
            defaultAreaPaths = ResolveDefaultAreaPaths(ctx);

        var parameters = new QueryParameters
        {
            SearchText = searchText,
            TypeFilter = type,
            StateFilter = state,
            TitleFilter = title,
            AssignedToFilter = assignedTo,
            AreaPathFilter = areaPath,
            IterationPathFilter = iterationPath,
            Top = top,
            DefaultAreaPaths = defaultAreaPaths,
        };

        var wiql = WiqlQueryBuilder.Build(parameters);

        IReadOnlyList<int> ids;
        try { ids = await ctx.AdoService.QueryByWiqlAsync(wiql, top, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Query failed: {ex.Message}"); }

        IReadOnlyList<WorkItem> items = ids.Count > 0
            ? await ctx.AdoService.FetchBatchAsync(ids, ct)
            : [];

        // Best-effort cache write — ADO is the source of truth
        if (items.Count > 0)
        {
            try { await ctx.WorkItemRepo.SaveBatchAsync(items, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
        }

        var queryDescription = BuildQueryDescription(parameters);
        return McpResultBuilder.FormatQueryResults(items, items.Count >= top, queryDescription, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_children"), Description("List the direct children of a work item by ID")]
    public async Task<CallToolResult> Children(
        [Description("Parent work item ID")] int id,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        var children = await ctx.WorkItemRepo.GetChildrenAsync(id, ct);
        return McpResultBuilder.FormatChildren(id, children, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_parent"), Description("Get the parent of a work item by ID")]
    public async Task<CallToolResult> Parent(
        [Description("Child work item ID")] int id,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        var (childResult, fetchErr) = await FetchWithFallbackAsync(ctx, id, ct);
        if (fetchErr is not null) return fetchErr;
        var child = childResult!;

        // Resolve parent — cache-first, ADO fallback (best-effort: null if fetch fails)
        WorkItem? parent = null;
        if (child.ParentId.HasValue)
        {
            parent = await ctx.WorkItemRepo.GetByIdAsync(child.ParentId.Value, ct);
            if (parent is null)
            {
                try
                {
                    parent = await ctx.AdoService.FetchAsync(child.ParentId.Value, ct);
                    await ctx.WorkItemRepo.SaveAsync(parent, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
            }
        }

        return McpResultBuilder.FormatParent(child, parent, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_sprint"), Description("Get the current sprint iteration info, optionally listing sprint items")]
    public async Task<CallToolResult> Sprint(
        [Description("When true, includes work items assigned to the current sprint")] bool items = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        IterationPath iterationPath;
        try { iterationPath = await ctx.IterationService.GetCurrentIterationAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Failed to get current iteration: {ex.Message}"); }

        IReadOnlyList<WorkItem>? sprintItems = null;
        if (items)
            sprintItems = await ctx.WorkItemRepo.GetByIterationAsync(iterationPath, ct);

        return McpResultBuilder.FormatSprint(iterationPath, sprintItems, ctx.Key.ToString());
    }

    private CallToolResult? TryResolve(string? workspace, out WorkspaceContext ctx)
    {
        try { ctx = resolver.Resolve(workspace); return null; }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException)
        { ctx = null!; return McpResultBuilder.ToError(ex.Message); }
    }

    private async Task<(WorkItem? Item, CallToolResult? Error)> FetchWithFallbackAsync(
        WorkspaceContext ctx, int id, CancellationToken ct)
    {
        var item = await ctx.WorkItemRepo.GetByIdAsync(id, ct);
        if (item is not null) return (item, null);
        try
        {
            item = await ctx.AdoService.FetchAsync(id, ct);
            await ctx.WorkItemRepo.SaveAsync(item, ct);
            return (item, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, McpResultBuilder.ToError($"Work item #{id} not found in cache or ADO: {ex.Message}"));
        }
    }

    private static IReadOnlyList<(string Path, bool IncludeChildren)>? ResolveDefaultAreaPaths(WorkspaceContext ctx)
    {
        var entries = ctx.Config.Defaults?.AreaPathEntries;
        if (entries is { Count: > 0 })
            return entries.Select(e => (e.Path, e.IncludeChildren)).ToList();

        var areaPaths = ctx.Config.Defaults?.AreaPaths;
        if (areaPaths is null || areaPaths.Count == 0)
        {
            var singlePath = ctx.Config.Defaults?.AreaPath;
            if (!string.IsNullOrWhiteSpace(singlePath))
                areaPaths = [singlePath];
        }

        if (areaPaths is { Count: > 0 })
            return areaPaths.Select(p => (p, true)).ToList();

        return null;
    }

    private static string BuildQueryDescription(QueryParameters parameters)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(parameters.SearchText))
            parts.Add($"title or description contains '{parameters.SearchText}'");
        if (!string.IsNullOrEmpty(parameters.TitleFilter))
            parts.Add($"title contains '{parameters.TitleFilter}'");
        if (!string.IsNullOrEmpty(parameters.TypeFilter))
            parts.Add($"type = '{parameters.TypeFilter}'");
        if (!string.IsNullOrEmpty(parameters.StateFilter))
            parts.Add($"state = '{parameters.StateFilter}'");
        if (!string.IsNullOrEmpty(parameters.AssignedToFilter))
            parts.Add($"assignedTo = '{parameters.AssignedToFilter}'");
        if (!string.IsNullOrEmpty(parameters.AreaPathFilter))
            parts.Add($"areaPath under '{parameters.AreaPathFilter}'");
        if (!string.IsNullOrEmpty(parameters.IterationPathFilter))
            parts.Add($"iterationPath under '{parameters.IterationPathFilter}'");

        return parts.Count > 0 ? string.Join(" AND ", parts) : "all items";
    }
}
