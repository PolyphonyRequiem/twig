using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.Field;
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
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var (item, fetchErr) = await ctx.FetchWithFallbackAsync(id, ct);
        if (fetchErr is not null) return McpResultBuilder.ToError(fetchErr);

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
        [Description("Only items created within this many days")] int? createdSince = null,
        [Description("Only items changed within this many days")] int? changedSince = null,
        [Description("Maximum results to return (default: 25)")] int top = 25,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        // Resolve default area paths from config when no explicit area path filter is given
        IReadOnlyList<(string Path, bool IncludeChildren)>? defaultAreaPaths = null;
        if (string.IsNullOrWhiteSpace(areaPath))
            defaultAreaPaths = ctx.Config.Defaults?.ResolveAreaPaths();

        var parameters = new QueryParameters
        {
            SearchText = searchText,
            TypeFilter = type,
            StateFilter = state,
            TitleFilter = title,
            AssignedToFilter = assignedTo,
            AreaPathFilter = areaPath,
            IterationPathFilter = iterationPath,
            CreatedSinceDays = createdSince,
            ChangedSinceDays = changedSince,
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
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var children = await ctx.WorkItemRepo.GetChildrenAsync(id, ct);
        return McpResultBuilder.FormatChildren(id, children, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_parent"), Description("Get the parent of a work item by ID")]
    public async Task<CallToolResult> Parent(
        [Description("Child work item ID")] int id,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var (childResult, fetchErr) = await ctx.FetchWithFallbackAsync(id, ct);
        if (fetchErr is not null) return McpResultBuilder.ToError(fetchErr);
        var child = childResult!;

        // Resolve parent — cache-first, ADO fallback (best-effort: null if fetch fails)
        WorkItem? parent = null;
        if (child.ParentId.HasValue)
        {
            var (p, _) = await ctx.FetchWithFallbackAsync(child.ParentId.Value, ct);
            parent = p;
        }

        return McpResultBuilder.FormatParent(child, parent, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_verify_descendants"), Description("Recursively verify that all descendants of a work item are in terminal states.")]
    public async Task<CallToolResult> VerifyDescendants(
        [Description("Root work item ID")] int id,
        [Description("Maximum depth to traverse (default: 2)")] int maxDepth = 2,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var service = new DescendantVerificationService(
            ctx.WorkItemRepo, ctx.AdoService, ctx.ProcessConfigProvider);

        var result = await service.VerifyAsync(id, maxDepth, ct);
        return McpResultBuilder.FormatVerification(result, ctx.Key.ToString());
    }

    [McpServerTool(Name = "twig_sprint"), Description("Get the current sprint iteration info, optionally listing sprint items")]
    public async Task<CallToolResult> Sprint(
        [Description("When true, includes work items assigned to the current sprint")] bool items = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        IterationPath iterationPath;
        try { iterationPath = await ctx.IterationService.GetCurrentIterationAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Failed to get current iteration: {ex.Message}"); }

        IReadOnlyList<WorkItem>? sprintItems = null;
        if (items)
            sprintItems = await ctx.WorkItemRepo.GetByIterationAsync(iterationPath, ct);

        return McpResultBuilder.FormatSprint(iterationPath, sprintItems, ctx.Key.ToString());
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
        if (parameters.CreatedSinceDays.HasValue)
            parts.Add($"created within {parameters.CreatedSinceDays.Value}d");
        if (parameters.ChangedSinceDays.HasValue)
            parts.Add($"changed within {parameters.ChangedSinceDays.Value}d");

        return parts.Count > 0 ? string.Join(" AND ", parts) : "all items";
    }
}
