using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Extensions;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for work item creation: twig_new, twig_link.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class CreationTools(WorkspaceResolver resolver, SeedFactory seedFactory)
{
    [McpServerTool(Name = "twig_new"), Description("Create a new work item in Azure DevOps")]
    public async Task<CallToolResult> New(
        [Description("Work item type (e.g. Epic, Issue, Task, Bug, User Story)")] string type,
        [Description("Title for the new work item")] string title,
        [Description("Parent work item ID (optional — used for path inheritance and child type validation)")] int? parentId = null,
        [Description("Description text (optional — treated as Markdown and converted to HTML)")] string? description = null,
        [Description("Assignee display name (optional)")] string? assignedTo = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true and parentId is provided, skips the duplicate title+type check. Default is false (dedup enabled).")] bool skipDuplicateCheck = false,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Title is required.");

        var parseResult = WorkItemType.Parse(type);
        if (!parseResult.IsSuccess)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Type is required. Provide a work item type (e.g. Epic, Issue, Task).");

        var parsedType = parseResult.Value;

        if (parentId is <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"parentId must be a positive work item ID (got {parentId.Value}).");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (parentId.HasValue)
        {
            if (!skipDuplicateCheck)
            {
                var dupeResult = await CheckForDuplicateAsync(ctx, parentId.Value, title, parsedType, ct);
                if (dupeResult is not null) return await EnvelopeBuilder.WrapAsync(ctx, dupeResult, verbose, ct);
            }

            var parentedResult = await CreateParentedAsync(ctx, parentId.Value, title, parsedType, description, assignedTo, ct);
            return await EnvelopeBuilder.WrapAsync(ctx, parentedResult, verbose, ct);
        }

        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();

        // Validate type is recognized in the process configuration
        if (!processConfig.TypeConfigs.ContainsKey(parsedType))
        {
            var validTypes = string.Join(", ", processConfig.TypeConfigs.Keys);
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                $"Unknown work item type '{type}'. Valid types: {validTypes}.", ctx, ct);
        }

        // No parent — use workspace defaults for area/iteration paths
        var areaPath = ResolveDefaultPath(ctx.Config.Defaults?.AreaPath, ctx.Config.Project, AreaPath.Parse);
        var iterationPath = ResolveDefaultPath(ctx.Config.Defaults?.IterationPath, ctx.Config.Project, IterationPath.Parse);

        var unparentedResult = seedFactory.CreateUnparented(
            title,
            parsedType,
            areaPath,
            iterationPath,
            assignedTo);

        if (!unparentedResult.IsSuccess)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, unparentedResult.Error, ctx, ct);

        var seed = unparentedResult.Value;

        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", MarkdownConverter.ToHtml(description));

        int newId;
        try { newId = await ctx.AdoService.CreateAsync(seed.ToCreateRequest(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Create failed: {ex.Message}", ctx, ct); }

        // Fetch back the created item for confirmation
        WorkItem created;
        try { created = await ctx.AdoService.FetchAsync(newId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable,
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run twig_sync to recover.", ctx, ct);
        }

        try { await ctx.WorkItemRepo.SaveAsync(created, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var url = $"https://dev.azure.com/{ctx.Key.Org}/{ctx.Key.Project}/_workitems/edit/{created.Id}";
        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatCreated(created, url, ctx.Key.ToString()), verbose, ct);
    }

    [McpServerTool(Name = "twig_find_or_create"), Description("Find an existing work item by title and type under a parent, or create it if not found. Always performs a deduplication check — use this instead of twig_new when idempotent creation is required.")]
    public async Task<CallToolResult> FindOrCreate(
        [Description("Work item type (e.g. Epic, Issue, Task, Bug, User Story)")] string type,
        [Description("Title for the work item to find or create")] string title,
        [Description("Parent work item ID — required for scoped dedup check")] int parentId,
        [Description("Description text (optional — treated as Markdown and converted to HTML)")] string? description = null,
        [Description("Assignee display name (optional)")] string? assignedTo = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        return await New(type, title, parentId, description, assignedTo, workspace, skipDuplicateCheck: false, verbose: verbose, ct);
    }

    [McpServerTool(Name = "twig_link"), Description("Create a relationship between two work items")]
    public async Task<CallToolResult> Link(
        [Description("Source work item ID")] int sourceId,
        [Description("Target work item ID")] int targetId,
        [Description("Relationship type (parent, child, related, predecessor, successor)")] string linkType,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (sourceId <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"sourceId must be a positive work item ID (got {sourceId}).");

        if (targetId <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"targetId must be a positive work item ID (got {targetId}).");

        if (sourceId == targetId)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "sourceId and targetId must be different work items.");

        var supportedTypes = string.Join(", ", LinkTypeMapper.SupportedTypes);
        if (string.IsNullOrWhiteSpace(linkType))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"linkType is required. Supported types: {supportedTypes}.");

        if (!LinkTypeMapper.TryResolve(linkType, out var adoLinkType))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Unknown link type '{linkType}'. Supported types: {supportedTypes}.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        try
        {
            await ctx.AdoService.AddLinkAsync(sourceId, targetId, adoLinkType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Link failed: {ex.Message}", ctx, ct);
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

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatLinked(sourceId, targetId, linkType, warning), verbose, ct);
    }

    [McpServerTool(Name = "twig_link_branch"), Description("Link a git branch to a work item as an ADO artifact link")]
    public async Task<CallToolResult> LinkBranch(
        [Description("Work item ID to link the branch to")] int workItemId,
        [Description("Branch name (e.g. feature/123-fix-login)")] string branchName,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (workItemId <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"workItemId must be a positive work item ID (got {workItemId}).");

        if (string.IsNullOrWhiteSpace(branchName))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "branchName is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.BranchLinkService is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, "Git context is not configured for this workspace.", ctx, ct);

        var result = await ctx.BranchLinkService.LinkBranchAsync(workItemId, branchName, ct);
        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatBranchLinked(result), verbose, ct);
    }

    [McpServerTool(Name = "twig_link_artifact"), Description("Add an artifact link (URL or vstfs:// URI) to a work item")]
    public async Task<CallToolResult> LinkArtifact(
        [Description("Work item ID to link the artifact to")] int workItemId,
        [Description("Artifact URL (http/https) or vstfs:// URI")] string url,
        [Description("Display name for the link (optional)")] string? name = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (workItemId <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"workItemId must be a positive work item ID (got {workItemId}).");

        if (string.IsNullOrWhiteSpace(url))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "url is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        bool alreadyLinked;
        try
        {
            alreadyLinked = await ctx.AdoService.AddArtifactLinkAsync(workItemId, url, name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Link failed: {ex.Message}", ctx, ct);
        }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatArtifactLinked(workItemId, url, alreadyLinked), verbose, ct);
    }

    private async Task<CallToolResult?> CheckForDuplicateAsync(
        WorkspaceContext ctx, int parentId, string title, WorkItemType type, CancellationToken ct)
    {
        WorkItem? existing;
        try { existing = await DuplicateGuard.FindExistingChildAsync(ctx.AdoService, parentId, title, type, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { existing = null; }

        if (existing is null) return null;

        var url = $"https://dev.azure.com/{ctx.Key.Org}/{ctx.Key.Project}/_workitems/edit/{existing.Id}";
        return McpResultBuilder.FormatFoundExisting(existing, url, ctx.Key.ToString());
    }

    private async Task<CallToolResult> CreateParentedAsync(
        WorkspaceContext ctx,
        int parentId,
        string title,
        WorkItemType parsedType,
        string? description,
        string? assignedTo,
        CancellationToken ct)
    {
        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();

        var (parent, fetchErr) = await ctx.FetchWithFallbackAsync(parentId, ct);
        if (fetchErr is not null) return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, fetchErr, ctx, ct);

        var seedResult = seedFactory.Create(title, parent!, processConfig, parsedType, assignedTo);
        if (!seedResult.IsSuccess)
        {
            var parentType = parent!.Type;
            var allowed = processConfig.GetAllowedChildTypes(parentType);
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                $"{seedResult.Error} Allowed child types for {parentType}: " +
                (allowed.Count > 0 ? string.Join(", ", allowed) : "(none)") + ".", ctx, ct);
        }

        var seed = seedResult.Value;

        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", MarkdownConverter.ToHtml(description));

        int newId;
        try { newId = await ctx.AdoService.CreateAsync(seed.ToCreateRequest(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Create failed: {ex.Message}", ctx, ct); }

        WorkItem created;
        try { created = await ctx.AdoService.FetchAsync(newId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable,
                $"Created #{newId} in ADO but fetch-back failed: {ex.Message}. Run twig_sync to recover.", ctx, ct);
        }

        try { await ctx.WorkItemRepo.SaveAsync(created, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        var url = $"https://dev.azure.com/{ctx.Key.Org}/{ctx.Key.Project}/_workitems/edit/{created.Id}";
        return McpResultBuilder.FormatCreated(created, url, ctx.Key.ToString());
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