using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for work item creation: twig_new, twig_link.
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

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        ProcessConfiguration processConfig;
        try { processConfig = ctx.ProcessConfigProvider.GetConfiguration(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return McpResultBuilder.ToError($"Failed to load process configuration: {ex.Message}"); }

        WorkItem seed;
        if (parentId.HasValue)
        {
            // Fetch parent for path inheritance and type validation
            var (parent, fetchErr) = await ctx.FetchWithFallbackAsync(parentId.Value, ct);
            if (fetchErr is not null) return McpResultBuilder.ToError(fetchErr);

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
            var areaPath = ResolveDefaultPath(ctx.Config.Defaults?.AreaPath, ctx.Config.Project, AreaPath.Parse);
            var iterationPath = ResolveDefaultPath(ctx.Config.Defaults?.IterationPath, ctx.Config.Project, IterationPath.Parse);

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

    [McpServerTool(Name = "twig_link"), Description("Create a relationship between two work items")]
    public async Task<CallToolResult> Link(
        [Description("Source work item ID")] int sourceId,
        [Description("Target work item ID")] int targetId,
        [Description("Relationship type (parent, child, related, predecessor, successor)")] string linkType,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (sourceId <= 0)
            return McpResultBuilder.ToError($"sourceId must be a positive work item ID (got {sourceId}).");

        if (targetId <= 0)
            return McpResultBuilder.ToError($"targetId must be a positive work item ID (got {targetId}).");

        if (sourceId == targetId)
            return McpResultBuilder.ToError("sourceId and targetId must be different work items.");

        if (string.IsNullOrWhiteSpace(linkType))
            return McpResultBuilder.ToError(
                $"linkType is required. Supported types: {string.Join(", ", LinkTypeMapper.SupportedTypes)}.");

        if (!LinkTypeMapper.TryResolve(linkType, out var adoLinkType))
            return McpResultBuilder.ToError(
                $"Unknown link type '{linkType}'. Supported types: {string.Join(", ", LinkTypeMapper.SupportedTypes)}.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        try
        {
            await ctx.AdoService.AddLinkAsync(sourceId, targetId, adoLinkType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpResultBuilder.ToError($"Link failed: {ex.Message}");
        }

        // Best-effort: refresh local link cache for both items
        string? warning = null;
        try
        {
            await ctx.SyncCoordinatorFactory.ReadOnly.SyncLinksAsync(sourceId, ct);
            await ctx.SyncCoordinatorFactory.ReadOnly.SyncLinksAsync(targetId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warning = $"Link created but cache sync failed: {ex.Message}. Run twig_sync to recover.";
        }

        return McpResultBuilder.FormatLinked(sourceId, targetId, linkType, warning);
    }

    private static T ResolveDefaultPath<T>(string? configPath, string? projectName, Func<string?, Result<T>> parse) where T : struct
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var r = parse(configPath);
            if (r.IsSuccess) return r.Value;
        }
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            var r = parse(projectName);
            if (r.IsSuccess) return r.Value;
        }
        return default;
    }
}
