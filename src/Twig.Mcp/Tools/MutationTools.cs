using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;
using Twig.Infrastructure.Serialization;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for mutations: twig_state, twig_update, twig_patch, twig_note, twig_delete, twig_sync.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class MutationTools(WorkspaceResolver resolver)
{
    /// <summary>
    /// Resolves a work item either by explicit ID (cache+ADO fallback, no context change)
    /// or via the active item resolver (current active context).
    /// </summary>
    private static async Task<(WorkItem? Item, CallToolResult? Error)> ResolveWorkItemAsync(
        WorkspaceContext ctx, int? id, CancellationToken ct)
    {
        if (id.HasValue)
        {
            var (item, error) = await ctx.FetchWithFallbackAsync(id.Value, ct);
            if (item is null)
                return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, error ?? $"Work item #{id.Value} not found.", ctx, ct));
            return (item, null);
        }

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.NoContext, "No active work item. Use twig_set to set context.", ctx, ct));
        if (resolved is ActiveItemResult.Unreachable u)
            return (null, await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Work item #{u.Id} not found in cache.", ctx, ct));

        var activeItem = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;
        return (activeItem, null);
    }

    [McpServerTool(Name = "twig_state"), Description("Change the state of a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> State(
        [Description("Target state name (full or partial, case-insensitive)")] string stateName,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_state requires a target state name (e.g. Active, Closed, Resolved).");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Seed routing: local-only mutation, no process config or ADO interaction.
        if (item.IsSeed)
        {
            var previousState = item.State;
            var change = new FieldChange("System.State", item.State, stateName);
            var seedProvider = new SeedMutationProvider(ctx.WorkItemRepo);
            var seedResult = await seedProvider.ChangeStateAsync(item.Id, change, ct);
            if (!seedResult.IsSuccess)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, seedResult.ErrorMessage!, ctx, ct);

            // Re-read the updated item for the response
            var seedUpdated = await ctx.WorkItemRepo.GetByIdAsync(item.Id, ct) ?? item;

            try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

            return await EnvelopeBuilder.WrapAsync(ctx,
                McpResultBuilder.FormatStateChange(seedUpdated, previousState), verbose, ct);
        }

        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.CacheStale, $"No process configuration found for type '{item.Type}'.", ctx, ct);

        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidStateTransition, resolveResult.Error, ctx, ct);

        var newState = resolveResult.Value;
        var previousStateAdo = item.State;

        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
            return await EnvelopeBuilder.SuccessAsync(ctx, w =>
            {
                w.WriteString("message", $"Already in state '{newState}'.");
                w.WriteString("state", newState);
            }, verbose, ct);

        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);

        if (!transition.IsAllowed)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidStateTransition, $"Transition from '{item.State}' to '{newState}' is not allowed.", ctx, ct);

        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            var changes = new[] { new FieldChange("System.State", item.State, newState) };
            await ConflictRetryHelper.PatchWithRetryAsync(ctx.AdoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, ctx.PendingChangeStore, ctx.AdoService); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Resync cache — best-effort, non-fatal
        WorkItem updated;
        try
        {
            updated = await ctx.AdoService.FetchAsync(item.Id, ct);
            await ctx.WorkItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
        }

        // Parent propagation: if child moved to InProgress, activate parent if still Proposed.
        // The service is best-effort and never throws (except OperationCanceledException).
        var newCategory = StateCategoryResolver.Resolve(newState, typeConfig.StateEntries);
        if (newCategory == StateCategory.InProgress)
            _ = await ctx.ParentPropagationService.TryPropagateToParentAsync(updated, StateCategory.InProgress, ct);

        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatStateChange(updated, previousStateAdo), verbose, ct);
    }

    [McpServerTool(Name = "twig_update"), Description("Update a field on a work item and push to ADO. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. System.Title, System.Description, Microsoft.VSTS.Scheduling.StoryPoints)")] string field,
        [Description("New field value")] string value,
        [Description("Set to 'markdown' to convert the value from Markdown to HTML before storing (useful for System.Description)")] string? format = null,
        [Description("When true, append the value to the existing field content instead of replacing it")] bool append = false,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(field) || value is null)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_update requires a field name and value.");

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Unknown format '{format}'. Supported formats: markdown");

        var effectiveValue = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            ? MarkdownConverter.ToHtml(value)
            : value;

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Seed routing: local-only mutation, no ADO interaction.
        if (item.IsSeed)
        {
            if (append)
            {
                item.Fields.TryGetValue(field, out var existingValue);
                effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: format is not null);
            }

            var change = new FieldChange(field, null, effectiveValue);
            var seedProvider = new SeedMutationProvider(ctx.WorkItemRepo);
            var seedResult = await seedProvider.UpdateFieldAsync(item.Id, change, ct);
            if (!seedResult.IsSuccess)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, seedResult.ErrorMessage!, ctx, ct);

            try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

            return await EnvelopeBuilder.WrapAsync(ctx,
                McpResultBuilder.FormatFieldUpdate(item, field, value), verbose, ct);
        }

        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);

            if (append)
            {
                remote.Fields.TryGetValue(field, out var existingValue);
                effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: format is not null);
            }

            var changes = new[] { new FieldChange(field, null, effectiveValue) };
            await ConflictRetryHelper.PatchWithRetryAsync(ctx.AdoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, ctx.PendingChangeStore, ctx.AdoService); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Resync cache — best-effort, non-fatal
        WorkItem updated;
        try
        {
            updated = await ctx.AdoService.FetchAsync(item.Id, ct);
            await ctx.WorkItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
        }

        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatFieldUpdate(updated, field, value), verbose, ct);
    }

    [McpServerTool(Name = "twig_patch"), Description("Atomically patch multiple fields on a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Patch(
        [Description("JSON object with field reference name → value pairs (e.g. {\"System.Title\":\"New\",\"System.Description\":\"Desc\"})")] string fields,
        [Description("Convert values before sending. Supported: \"markdown\" (converts Markdown to HTML)")] string? format = null,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_patch requires a non-empty JSON object of field name → value pairs.");

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Unknown format '{format}'. Supported formats: markdown");

        // Parse JSON into field dictionary (AOT-safe via source-generated context)
        Dictionary<string, string>? fieldMap;
        try
        {
            fieldMap = JsonSerializer.Deserialize(fields, TwigJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException ex)
        {
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, $"Invalid JSON: {ex.Message}");
        }

        if (fieldMap is null || fieldMap.Count == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "JSON must be a non-empty object with field name → value pairs.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Build FieldChange[] with optional markdown conversion
        var changes = new List<FieldChange>(fieldMap.Count);
        var fieldChanges = new Dictionary<string, (string? OldValue, string? NewValue)>(fieldMap.Count);
        foreach (var (key, value) in fieldMap)
        {
            var effectiveValue = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
                ? MarkdownConverter.ToHtml(value)
                : value;
            changes.Add(new FieldChange(key, null, effectiveValue));
            fieldChanges[key] = (null, effectiveValue);
        }

        // Seed routing: apply each field change via SeedMutationProvider.
        if (item.IsSeed)
        {
            var seedProvider = new SeedMutationProvider(ctx.WorkItemRepo);
            foreach (var change in changes)
            {
                var seedResult = await seedProvider.UpdateFieldAsync(item.Id, change, ct);
                if (!seedResult.IsSuccess)
                    return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, $"Field '{change.FieldName}' failed: {seedResult.ErrorMessage}", ctx, ct);
            }

            try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

            return await EnvelopeBuilder.WrapAsync(ctx,
                McpResultBuilder.FormatPatch(item, fieldChanges), verbose, ct);
        }

        // Fetch remote and PATCH with conflict retry
        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            await ConflictRetryHelper.PatchWithRetryAsync(ctx.AdoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, ctx.PendingChangeStore, ctx.AdoService); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Resync cache — best-effort, non-fatal
        WorkItem updated;
        try
        {
            updated = await ctx.AdoService.FetchAsync(item.Id, ct);
            await ctx.WorkItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
        }

        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatPatch(updated, fieldChanges), verbose, ct);
    }

    [McpServerTool(Name = "twig_note"), Description("Add a comment/note to a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add as a comment")] string text,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_note requires non-empty text.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Push comment to ADO— fall back to local staging on failure
        bool isPending;
        try
        {
            await ctx.AdoService.AddCommentAsync(item.Id, text, ct);
            isPending = false;

            await ctx.PendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ctx.PendingChangeStore.AddChangeAsync(item.Id, "note", fieldName: null, oldValue: null, newValue: text, ct);
            isPending = true;
        }

        // Resync cache — best-effort, only on successful push
        if (!isPending)
        {
            try
            {
                var updated = await ctx.AdoService.FetchAsync(item.Id, ct);
                await ctx.WorkItemRepo.SaveAsync(updated, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best-effort
            }
        }

        await ctx.PromptStateWriter.WritePromptStateAsync();

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatNoteAdded(item.Id, item.Title, isPending), verbose, ct);
    }

    [McpServerTool(Name = "twig_delete"), Description("Permanently delete a work item from Azure DevOps (two-phase: first call returns confirmation prompt, second call with confirmed=true executes deletion)")]
    public async Task<CallToolResult> Delete(
        [Description("The work item ID to delete")] int id,
        [Description("Set to true to confirm and execute the deletion. Omit or set false for the confirmation prompt.")] bool confirmed = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (id <= 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_delete requires a positive work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // Resolve item from cache or ADO
        var (item, fetchError) = await ctx.FetchWithFallbackAsync(id, ct);
        if (item is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, fetchError ?? $"Work item #{id} not found.", ctx, ct);

        // Seed guard
        if (item.IsSeed)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, $"#{id} is a seed. Use 'twig seed discard {id}' instead.", ctx, ct);

        // Fresh fetch with links from ADO
        WorkItem freshItem;
        IReadOnlyList<WorkItemLink> links;
        IReadOnlyList<WorkItem> children;
        try
        {
            (freshItem, links) = await ctx.AdoService.FetchWithLinksAsync(id, ct);
            children = await ctx.AdoService.FetchChildrenAsync(id, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        // Link guard — refuse if any links exist
        var linkCount = (freshItem.ParentId.HasValue ? 1 : 0) + children.Count + links.Count;
        if (linkCount > 0)
        {
            var parts = new List<string>();
            if (freshItem.ParentId.HasValue) parts.Add("1 parent");
            if (children.Count > 0) parts.Add($"{children.Count} child{(children.Count != 1 ? "ren" : "")}");
            if (links.Count > 0)
            {
                var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in links)
                {
                    byType.TryGetValue(link.LinkType, out var c);
                    byType[link.LinkType] = c + 1;
                }
                foreach (var (lt, c) in byType)
                    parts.Add($"{c} {lt.ToLowerInvariant()}");
            }

            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                $"Cannot delete #{id} '{freshItem.Title}' — it has {linkCount} link(s): {string.Join(", ", parts)}. " +
                "Remove all links before deleting. Consider 'twig_state Closed' instead — it preserves history and is reversible.",
                ctx, ct);
        }

        // Phase 1: Confirmation prompt
        if (!confirmed)
            return await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDeleteConfirmation(freshItem), verbose, ct);

        // Phase 2: Execute deletion

        // Audit trail — best-effort note on parent
        if (freshItem.ParentId.HasValue)
        {
            try
            {
                await ctx.AdoService.AddCommentAsync(
                    freshItem.ParentId.Value,
                    $"Child work item #{id} '{freshItem.Title}' ({freshItem.Type}) was deleted via twig.",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort — parent may be inaccessible
            }
        }

        // Delete from ADO
        try
        {
            await ctx.AdoService.DeleteAsync(id, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Delete failed: {ex.Message}", ctx, ct);
        }

        // Cache cleanup
        await ctx.WorkItemRepo.DeleteByIdAsync(id, ct);
        await ctx.PendingChangeStore.ClearChangesAsync(id, ct);

        // Prompt state refresh — best-effort
        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatDeleted(id, freshItem.Title), verbose, ct);
    }

    [McpServerTool(Name = "twig_discard"), Description("Discard pending local changes for a work item")]
    public async Task<CallToolResult> Discard(
        [Description("Work item ID (optional — defaults to the active work item)")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        int itemId;
        if (id.HasValue)
        {
            itemId = id.Value;
        }
        else
        {
            var activeId = await ctx.ContextStore.GetActiveWorkItemIdAsync(ct);
            if (activeId is null)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.NoContext, "No active work item. Use twig_set to set context.", ctx, ct);
            itemId = activeId.Value;
        }

        // Resolve item: cache-first, ADO fallback
        var item = await ctx.WorkItemRepo.GetByIdAsync(itemId, ct);
        if (item is null)
        {
            try { item = await ctx.AdoService.FetchAsync(itemId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Work item #{itemId} could not be resolved: {ex.Message}", ctx, ct);
            }
        }

        var (notes, fieldEdits) = await ctx.PendingChangeStore.GetChangeSummaryAsync(itemId, ct);

        if (notes == 0 && fieldEdits == 0)
            return await EnvelopeBuilder.WrapAsync(ctx,
                McpResultBuilder.FormatDiscardedNone(itemId, item.Title), verbose, ct);

        await ctx.PendingChangeStore.ClearChangesAsync(itemId, ct);
        await ctx.WorkItemRepo.ClearDirtyFlagAsync(itemId, ct);

        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatDiscarded(itemId, notes, fieldEdits), verbose, ct);
    }

    [McpServerTool(Name = "twig_sync"),Description("Flush pending local changes to ADO then refresh the local cache from ADO")]
    public async Task<CallToolResult> Sync(
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, skip the flush phase and only pull (refresh) from ADO.")] bool pull_only = false,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // Phase 1 — Push: flush all pending changes to ADO (skipped when pull_only)
        McpFlushSummary? flushSummary = null;
        if (!pull_only)
        {
            flushSummary = await ctx.Flusher.FlushAllAsync(ct);
        }

        // Phase 2 — Pull: sync active item context from ADO
        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.Found or ActiveItemResult.FetchedFromAdo)
        {
            var item = resolved is ActiveItemResult.Found f
                ? f.WorkItem
                : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

            var idsToSync = new List<int> { item.Id };

            if (item.ParentId.HasValue)
            {
                var chain = await ctx.WorkItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
                idsToSync.AddRange(chain.Select(p => p.Id));
            }

            var children = await ctx.WorkItemRepo.GetChildrenAsync(item.Id, ct);
            idsToSync.AddRange(children.Select(c => c.Id));

            try
            {
                await ctx.SyncCoordinatorFactory.ReadWrite.SyncItemSetAsync(idsToSync.Distinct().ToList(), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        await ctx.PromptStateWriter.WritePromptStateAsync();

        return await EnvelopeBuilder.WrapAsync(ctx,
            McpResultBuilder.FormatSyncSummary(flushSummary, pull_only), verbose, ct);
    }
}