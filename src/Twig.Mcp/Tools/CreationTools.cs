using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for work item creation: twig_new.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class CreationTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_new"), Description("Create a new work item in Azure DevOps")]
    public async Task<CallToolResult> New(
        [Description("Work item type (e.g. Epic, Issue, Task, Bug, User Story)")] string type,
        [Description("Title for the new work item")] string title,
        [Description("Parent work item ID (optional — used for path inheritance and child type validation)")] int? parentId = null,
        [Description("Description text (optional — treated as Markdown and converted to HTML)")] string? description = null,
        [Description("Assignee display name (optional)")] string? assignedTo = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return McpResultBuilder.ToError("Title is required. Usage: twig_new requires a non-empty title.");

        if (string.IsNullOrWhiteSpace(type))
            return McpResultBuilder.ToError("Type is required. Usage: twig_new requires a work item type (e.g. Epic, Issue, Task).");

        var parseResult = WorkItemType.Parse(type);
        if (!parseResult.IsSuccess)
            return McpResultBuilder.ToError(parseResult.Error);

        var parsedType = parseResult.Value;

        if (parentId is <= 0)
            return McpResultBuilder.ToError($"parentId must be a positive work item ID (got {parentId.Value}).");

        if (TryResolve(workspace, out var ctx) is { } resErr) return resErr;

        ProcessConfiguration processConfig;
        try { processConfig = ctx.ProcessConfigProvider.GetConfiguration(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Failed to load process configuration: {ex.Message}"); }

        WorkItem seed;
        if (parentId.HasValue)
        {
            // Fetch parent for path inheritance and type validation
            var (parent, fetchErr) = await FetchWithFallbackAsync(ctx, parentId.Value, ct);
            if (fetchErr is not null) return fetchErr;

            var seedResult = SeedFactory.Create(
                title,
                parent!,
                processConfig,
                parsedType,
                assignedTo);

            if (!seedResult.IsSuccess)
            {
                var allowedChildren = processConfig.GetAllowedChildTypes(parent!.Type);
                var allowedList = allowedChildren.Count > 0
                    ? string.Join(", ", allowedChildren)
                    : "(none)";
                return McpResultBuilder.ToError(
                    $"{seedResult.Error} Allowed child types for {parent!.Type}: {allowedList}.");
            }

            seed = seedResult.Value;
        }
        else
        {
            // Validate type is recognized in the process configuration
            if (!processConfig.TypeConfigs.ContainsKey(parsedType))
            {
                var validTypes = string.Join(", ", processConfig.TypeConfigs.Keys);
                return McpResultBuilder.ToError(
                    $"Unknown work item type '{type}'. Valid types: {validTypes}.");
            }

            // No parent — use workspace defaults for area/iteration paths
            var areaPath = ResolveDefaultAreaPath(ctx);
            var iterationPath = ResolveDefaultIterationPath(ctx);

            var seedResult = SeedFactory.CreateUnparented(
                title,
                parsedType,
                areaPath,
                iterationPath,
                assignedTo);

            if (!seedResult.IsSuccess)
                return McpResultBuilder.ToError(seedResult.Error);

            seed = seedResult.Value;
        }

        // Apply description with markdown→HTML conversion
        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", MarkdownConverter.ToHtml(description));

        // Create in ADO
        int newId;
        try { newId = await ctx.AdoService.CreateAsync(seed, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Create failed: {ex.Message}"); }

        // Fetch back the created item for confirmation
        WorkItem created;
        try { created = await ctx.AdoService.FetchAsync(newId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpResultBuilder.ToError(
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run twig_sync to recover.");
        }

        // Best-effort cache write
        try { await ctx.WorkItemRepo.SaveAsync(created, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var url = $"https://dev.azure.com/{ctx.Key.Org}/{ctx.Key.Project}/_workitems/edit/{created.Id}";
        return McpResultBuilder.FormatCreated(created, url, ctx.Key.ToString());
    }

    private CallToolResult? TryResolve(string? workspace, out WorkspaceContext ctx)
    {
        try { ctx = resolver.Resolve(workspace); return null; }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException)
        { ctx = null!; return McpResultBuilder.ToError(ex.Message); }
    }

    private static async Task<(WorkItem? Item, CallToolResult? Error)> FetchWithFallbackAsync(
        WorkspaceContext ctx, int id, CancellationToken ct)
    {
        var item = await ctx.WorkItemRepo.GetByIdAsync(id, ct);
        if (item is not null) return (item, null);
        try
        {
            item = await ctx.AdoService.FetchAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, McpResultBuilder.ToError($"Parent #{id} not found in cache or ADO: {ex.Message}"));
        }

        // Best-effort cache warm — do not fail if SQLite is unavailable
        try { await ctx.WorkItemRepo.SaveAsync(item, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return (item, null);
    }

    private static AreaPath ResolveDefaultAreaPath(WorkspaceContext ctx)
    {
        var configPath = ctx.Config.Defaults?.AreaPath;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var result = AreaPath.Parse(configPath);
            if (result.IsSuccess) return result.Value;
        }

        // Fallback to project name
        if (!string.IsNullOrWhiteSpace(ctx.Config.Project))
        {
            var result = AreaPath.Parse(ctx.Config.Project);
            if (result.IsSuccess) return result.Value;
        }

        return default;
    }

    private static IterationPath ResolveDefaultIterationPath(WorkspaceContext ctx)
    {
        var configPath = ctx.Config.Defaults?.IterationPath;
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var result = IterationPath.Parse(configPath);
            if (result.IsSuccess) return result.Value;
        }

        // Fallback to project name
        if (!string.IsNullOrWhiteSpace(ctx.Config.Project))
        {
            var result = IterationPath.Parse(ctx.Config.Project);
            if (result.IsSuccess) return result.Value;
        }

        return default;
    }
}
