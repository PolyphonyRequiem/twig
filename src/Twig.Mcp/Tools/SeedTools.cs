using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for seed lifecycle: twig_seed_new, twig_seed_view, twig_seed_publish, twig_seed_validate, twig_seed_discard, twig_seed_chain.
/// Seeds are local-only draft work items with negative IDs.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class SeedTools(WorkspaceResolver resolver, SeedFactory seedFactory)
{
    [McpServerTool(Name = "twig_seed_new"), Description("Create a new local seed work item (no ADO interaction). Seeds are draft items with negative IDs that can be published later.")]
    public async Task<CallToolResult> SeedNew(
        [Description("Title for the new seed work item")] string title,
        [Description("Work item type (e.g. Task, Issue, Bug). Required when no parentId is provided; inferred from parent's allowed child types when omitted.")] string? type = null,
        [Description("Parent work item ID (positive for ADO items, negative for other seeds). Used for type inference and path inheritance.")] int? parentId = null,
        [Description("Description text (optional — treated as Markdown and converted to HTML)")] string? description = null,
        [Description("Assignee display name (optional)")] string? assignedTo = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Title is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // Initialize seed counter from DB to avoid ID collisions
        var minSeedId = await ctx.WorkItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedFactory.InitializeSeedCounter(minSeedId.Value);

        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();

        WorkItemType? typeOverride = null;
        if (type is not null)
        {
            var typeResult = WorkItemType.Parse(type);
            if (!typeResult.IsSuccess)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, typeResult.Error, ctx, ct);
            typeOverride = typeResult.Value;
        }

        Result<Domain.Aggregates.WorkItem> seedResult;

        if (parentId.HasValue)
        {
            // Fetch the parent for type inference and path inheritance
            var (parent, fetchErr) = await ctx.FetchWithFallbackAsync(parentId.Value, ct);
            if (fetchErr is not null)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, fetchErr, ctx, ct);

            seedResult = seedFactory.Create(title, parent!, processConfig, typeOverride, assignedTo);
            if (!seedResult.IsSuccess)
            {
                var allowedChildren = processConfig.GetAllowedChildTypes(parent!.Type);
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                    $"{seedResult.Error} Allowed child types for {parent!.Type}: " +
                    (allowedChildren.Count > 0 ? string.Join(", ", allowedChildren) : "(none)") + ".", ctx, ct);
            }
        }
        else
        {
            // No parent — explicit type is required
            if (typeOverride is null)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                    "Either parentId or type must be provided. When no parent is specified, an explicit type is required.", ctx, ct);

            var areaPath = ResolveDefaultPath(ctx.Config.Defaults?.AreaPath, ctx.Config.Project, AreaPath.Parse);
            var iterationPath = ResolveDefaultPath(ctx.Config.Defaults?.IterationPath, ctx.Config.Project, IterationPath.Parse);

            seedResult = seedFactory.CreateUnparented(title, typeOverride.Value, areaPath, iterationPath, assignedTo);
            if (!seedResult.IsSuccess)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, seedResult.Error, ctx, ct);
        }

        var seed = seedResult.Value;

        if (!string.IsNullOrWhiteSpace(description))
            seed.SetField("System.Description", MarkdownConverter.ToHtml(description));

        // Persist locally — no ADO interaction
        await ctx.WorkItemRepo.SaveAsync(seed, ct);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("id", seed.Id);
            writer.WriteString("title", seed.Title);
            writer.WriteString("type", seed.Type.ToString());
            writer.WriteBoolean("isSeed", true);

            if (seed.ParentId.HasValue)
                writer.WriteNumber("parentId", seed.ParentId.Value);
            else
                writer.WriteNull("parentId");

            writer.WriteString("assignedTo", seed.AssignedTo ?? "");
            writer.WriteString("areaPath", seed.AreaPath.ToString());
            writer.WriteString("iterationPath", seed.IterationPath.ToString());
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_seed_view"), Description("List all local seed work items grouped by parent. No network call — reads from local cache only.")]
    public async Task<CallToolResult> SeedView(
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var seeds = await ctx.WorkItemRepo.GetSeedsAsync(ct);

        // Group seeds by parentId
        var groups = seeds
            .GroupBy(s => s.ParentId)
            .OrderBy(g => g.Key ?? int.MaxValue)
            .ToList();

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("seedCount", seeds.Count);

            writer.WriteStartArray("groups");
            foreach (var group in groups)
            {
                writer.WriteStartObject();

                if (group.Key.HasValue)
                    writer.WriteNumber("parentId", group.Key.Value);
                else
                    writer.WriteNull("parentId");

                writer.WriteStartArray("seeds");
                foreach (var seed in group.OrderBy(s => s.SeedCreatedAt))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", seed.Id);
                    writer.WriteString("title", seed.Title);
                    writer.WriteString("type", seed.Type.ToString());
                    writer.WriteString("state", seed.State);
                    writer.WriteString("assignedTo", seed.AssignedTo ?? "");
                    if (seed.SeedCreatedAt.HasValue)
                        writer.WriteString("createdAt", seed.SeedCreatedAt.Value.ToString("o"));
                    else
                        writer.WriteString("createdAt", "");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_seed_publish"), Description("Publish seed(s) to Azure DevOps as real work items. Returns the remapped ADO work item ID(s). Use id for a single seed or all=true for batch publish in topological order.")]
    public async Task<CallToolResult> SeedPublish(
        [Description("Seed ID to publish (negative integer). Mutually exclusive with all=true.")] int? id = null,
        [Description("When true, publishes all seeds in topological order. Mutually exclusive with id.")] bool all = false,
        [Description("When true, bypasses validation rules.")] bool force = false,
        [Description("When true, returns a plan without making any API calls.")] bool dryRun = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!id.HasValue && !all)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Specify a seed id or set all=true.");

        if (id.HasValue && all)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Specify either id or all=true, not both.");

        if (id.HasValue && id.Value >= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Seed ID must be a negative integer (got {id.Value}). Only unpublished seeds can be published.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // Construct the publish orchestrator from per-workspace services
        var backlogOrderer = new BacklogOrderer(ctx.AdoService, ctx.FieldDefinitionStore);
        var orchestrator = new SeedPublishOrchestrator(
            ctx.WorkItemRepo,
            ctx.AdoService,
            ctx.SeedLinkRepo,
            ctx.PublishIdMapRepo,
            ctx.SeedPublishRulesProvider,
            ctx.UnitOfWork,
            backlogOrderer);

        var activeId = await ctx.ContextStore.GetActiveWorkItemIdAsync(ct);

        if (all)
            return await PublishAllAsync(ctx, orchestrator, activeId, force, dryRun, verbose, ct);

        return await PublishSingleAsync(ctx, orchestrator, activeId, id!.Value, force, dryRun, verbose, ct);
    }

    private static async Task<CallToolResult> PublishSingleAsync(
        WorkspaceContext ctx,
        SeedPublishOrchestrator orchestrator,
        int? activeId,
        int seedId,
        bool force,
        bool dryRun,
        bool verbose,
        CancellationToken ct)
    {
        SeedPublishResult result;
        try
        {
            result = await orchestrator.PublishAsync(seedId, force, dryRun, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable,
                $"Publish failed: {ex.Message}", ctx, ct);
        }

        // Update active context if the published seed was the active item
        if (result.Status == SeedPublishStatus.Created && activeId == seedId && result.NewId > 0 && !dryRun)
            await ctx.ContextStore.SetActiveWorkItemIdAsync(result.NewId, ct);

        if (!result.IsSuccess)
        {
            var errorCode = result.Status == SeedPublishStatus.ValidationFailed
                ? McpErrorCode.InvalidInput
                : McpErrorCode.InternalError;

            return await EnvelopeBuilder.ErrorAsync(errorCode,
                FormatPublishError(result), ctx, ct);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            WriteSinglePublishResult(writer, result);
        }, verbose, ct);
    }

    private static async Task<CallToolResult> PublishAllAsync(
        WorkspaceContext ctx,
        SeedPublishOrchestrator orchestrator,
        int? activeId,
        bool force,
        bool dryRun,
        bool verbose,
        CancellationToken ct)
    {
        SeedPublishBatchResult batchResult;
        try
        {
            batchResult = await orchestrator.PublishAllAsync(force, dryRun, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable,
                $"Batch publish failed: {ex.Message}", ctx, ct);
        }

        // Update active context if the active seed was published
        if (activeId.HasValue && !dryRun)
        {
            var published = batchResult.Results
                .FirstOrDefault(r => r.OldId == activeId.Value && r.Status == SeedPublishStatus.Created);
            if (published is not null && published.NewId > 0)
                await ctx.ContextStore.SetActiveWorkItemIdAsync(published.NewId, ct);
        }

        if (batchResult.HasErrors)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                FormatBatchErrors(batchResult), ctx, ct);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("publishedCount", batchResult.CreatedCount);
            writer.WriteNumber("skippedCount", batchResult.SkippedCount);
            writer.WriteBoolean("dryRun", dryRun);

            writer.WriteStartArray("results");
            foreach (var r in batchResult.Results)
            {
                writer.WriteStartObject();
                WriteSinglePublishResult(writer, r);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, verbose, ct);
    }

    private static void WriteSinglePublishResult(System.Text.Json.Utf8JsonWriter writer, SeedPublishResult result)
    {
        writer.WriteNumber("oldId", result.OldId);
        writer.WriteNumber("newId", result.NewId);
        writer.WriteString("title", result.Title);
        writer.WriteString("status", result.Status switch
        {
            SeedPublishStatus.Created => "created",
            SeedPublishStatus.Skipped => "skipped",
            SeedPublishStatus.DryRun => "dry_run",
            SeedPublishStatus.ValidationFailed => "validation_failed",
            SeedPublishStatus.Error => "error",
            _ => "unknown",
        });

        if (result.LinkWarnings.Count > 0)
        {
            writer.WriteStartArray("linkWarnings");
            foreach (var w in result.LinkWarnings)
                writer.WriteStringValue(w);
            writer.WriteEndArray();
        }
    }

    private static string FormatPublishError(SeedPublishResult result)
    {
        if (result.ValidationFailures.Count > 0)
        {
            var failures = string.Join("; ", result.ValidationFailures.Select(f => $"{f.Rule}: {f.Message}"));
            return $"Seed {result.OldId} ('{result.Title}') failed validation: {failures}";
        }

        return result.ErrorMessage ?? $"Seed {result.OldId} publish failed with status {result.Status}.";
    }

    private static string FormatBatchErrors(SeedPublishBatchResult batch)
    {
        var parts = new List<string>();

        foreach (var error in batch.CycleErrors)
            parts.Add(error);

        foreach (var error in batch.PreFlightErrors)
            parts.Add(error);

        foreach (var r in batch.Results.Where(r => !r.IsSuccess))
            parts.Add(FormatPublishError(r));

        return string.Join(" | ", parts);
    }

    [McpServerTool(Name = "twig_seed_validate"), Description("Run pre-publish validation checks on one or all seeds. Returns structured validation results without publishing.")]
    public async Task<CallToolResult> SeedValidate(
        [Description("Seed ID to validate (negative integer). Mutually exclusive with all=true.")] int? id = null,
        [Description("When true, validates all seeds. Mutually exclusive with id.")] bool all = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!id.HasValue && !all)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Specify a seed id or set all=true.");

        if (id.HasValue && all)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Specify either id or all=true, not both.");

        if (id.HasValue && id.Value >= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Seed ID must be a negative integer (got {id.Value}). Only seeds can be validated.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var rules = await ctx.SeedPublishRulesProvider.GetRulesAsync(ct);

        if (all)
        {
            var seeds = await ctx.WorkItemRepo.GetSeedsAsync(ct);
            var results = seeds.Select(s => SeedValidator.Validate(s, rules)).ToList();
            var passCount = results.Count(r => r.Passed);
            var failCount = results.Count - passCount;

            return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
            {
                writer.WriteNumber("totalCount", results.Count);
                writer.WriteNumber("passedCount", passCount);
                writer.WriteNumber("failedCount", failCount);

                writer.WriteStartArray("results");
                foreach (var r in results)
                {
                    writer.WriteStartObject();
                    WriteValidationResult(writer, r);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }, verbose, ct);
        }

        // Single seed validation
        var seed = await ctx.WorkItemRepo.GetByIdAsync(id!.Value, ct);
        if (seed is null || !seed.IsSeed)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound,
                $"Seed {id.Value} not found.", ctx, ct);

        var result = SeedValidator.Validate(seed, rules);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            WriteValidationResult(writer, result);
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_seed_discard"), Description("Remove a seed from local store with cascade deletion of child seeds. No ADO interaction — local only.")]
    public async Task<CallToolResult> SeedDiscard(
        [Description("Seed ID to discard (negative integer).")] int id,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (id >= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Seed ID must be a negative integer (got {id}). Only seeds can be discarded.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var orchestrator = new SeedDiscardOrchestrator(ctx.WorkItemRepo, ctx.SeedLinkRepo, ctx.ContextStore);
        var plan = await orchestrator.BuildDiscardPlanAsync(id, ct);

        if (plan is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound,
                $"Seed {id} not found or is not a seed.", ctx, ct);

        await orchestrator.ExecuteDiscardAsync(plan, ct);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("discardedId", plan.TargetId);
            writer.WriteString("discardedTitle", plan.TargetTitle);
            writer.WriteNumber("totalDiscarded", plan.AllIds.Count);
            writer.WriteNumber("descendantsDiscarded", plan.DescendantCount);

            writer.WriteStartArray("allDiscardedIds");
            foreach (var discardedId in plan.AllIds)
                writer.WriteNumberValue(discardedId);
            writer.WriteEndArray();
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_seed_chain"), Description("Create a sequence of seeds under the same parent. Returns all created seed IDs. Composable inside twig_batch parallel blocks.")]
    public async Task<CallToolResult> SeedChain(
        [Description("Parent work item ID (positive for ADO items, negative for other seeds).")] int parentId,
        [Description("Ordered list of titles for the seed chain.")] string[] titles,
        [Description("Work item type (e.g. Task, Issue). When omitted, inferred from parent's allowed child types.")] string? type = null,
        [Description("Assignee display name applied to all seeds (optional)")] string? assignedTo = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (titles.Length == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "At least one title is required.");

        if (titles.Any(string.IsNullOrWhiteSpace))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "All titles must be non-empty.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // Initialize seed counter from DB to avoid ID collisions
        var minSeedId = await ctx.WorkItemRepo.GetMinSeedIdAsync(ct);
        if (minSeedId.HasValue)
            seedFactory.InitializeSeedCounter(minSeedId.Value);

        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();

        WorkItemType? typeOverride = null;
        if (type is not null)
        {
            var typeResult = WorkItemType.Parse(type);
            if (!typeResult.IsSuccess)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, typeResult.Error, ctx, ct);
            typeOverride = typeResult.Value;
        }

        // Fetch the parent for type inference and path inheritance
        var (parent, fetchErr) = await ctx.FetchWithFallbackAsync(parentId, ct);
        if (fetchErr is not null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, fetchErr, ctx, ct);

        var createdSeeds = new List<Domain.Aggregates.WorkItem>();

        foreach (var title in titles)
        {
            var seedResult = seedFactory.Create(title, parent!, processConfig, typeOverride, assignedTo);
            if (!seedResult.IsSuccess)
            {
                var allowedChildren = processConfig.GetAllowedChildTypes(parent!.Type);
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                    $"{seedResult.Error} Allowed child types for {parent!.Type}: " +
                    (allowedChildren.Count > 0 ? string.Join(", ", allowedChildren) : "(none)") + ".", ctx, ct);
            }

            var seed = seedResult.Value;
            await ctx.WorkItemRepo.SaveAsync(seed, ct);
            createdSeeds.Add(seed);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("createdCount", createdSeeds.Count);
            writer.WriteNumber("parentId", parentId);

            writer.WriteStartArray("seeds");
            foreach (var seed in createdSeeds)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", seed.Id);
                writer.WriteString("title", seed.Title);
                writer.WriteString("type", seed.Type.ToString());
                writer.WriteString("assignedTo", seed.AssignedTo ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, verbose, ct);
    }

    private static void WriteValidationResult(System.Text.Json.Utf8JsonWriter writer, SeedValidationResult result)
    {
        writer.WriteNumber("seedId", result.SeedId);
        writer.WriteString("title", result.Title);
        writer.WriteBoolean("passed", result.Passed);

        writer.WriteStartArray("failures");
        foreach (var failure in result.Failures)
        {
            writer.WriteStartObject();
            writer.WriteString("rule", failure.Rule);
            writer.WriteString("message", failure.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
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
