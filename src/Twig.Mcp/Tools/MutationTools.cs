using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
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
    [McpServerTool(Name = "twig_state"), Description("Change the state of the active work item")]
    public async Task<CallToolResult> State(
        [Description("Target state name (full or partial, case-insensitive)")] string stateName,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return McpResultBuilder.ToError("Usage: twig_state requires a target state name (e.g. Active, Closed, Resolved).");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

        var processConfig = ctx.ProcessConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return McpResultBuilder.ToError($"No process configuration found for type '{item.Type}'.");

        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
            return McpResultBuilder.ToError(resolveResult.Error);

        var newState = resolveResult.Value;
        var previousState = item.State;

        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToResult($"Already in state '{newState}'.");

        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);

        if (!transition.IsAllowed)
            return McpResultBuilder.ToError($"Transition from '{item.State}' to '{newState}' is not allowed.");

        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            var changes = new[] { new FieldChange("System.State", item.State, newState) };
            await ConflictRetryHelper.PatchWithRetryAsync(ctx.AdoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return McpResultBuilder.ToError(ex.Message);
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

        return McpResultBuilder.FormatStateChange(updated, previousState);
    }

    [McpServerTool(Name = "twig_update"), Description("Update a field on the active work item and push to ADO")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. System.Title, System.Description, Microsoft.VSTS.Scheduling.StoryPoints)")] string field,
        [Description("New field value")] string value,
        [Description("Set to 'markdown' to convert the value from Markdown to HTML before storing (useful for System.Description)")] string? format = null,
        [Description("When true, append the value to the existing field content instead of replacing it")] bool append = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(field) || value is null)
            return McpResultBuilder.ToError("Usage: twig_update requires a field name and value.");

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToError($"Unknown format '{format}'. Supported formats: markdown");

        var effectiveValue = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            ? MarkdownConverter.ToHtml(value)
            : value;

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

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
            return McpResultBuilder.ToError(ex.Message);
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

        return McpResultBuilder.FormatFieldUpdate(updated, field, value);
    }

    [McpServerTool(Name = "twig_patch"), Description("Atomically patch multiple fields on the active work item")]
    public async Task<CallToolResult> Patch(
        [Description("JSON object with field reference name → value pairs (e.g. {\"System.Title\":\"New\",\"System.Description\":\"Desc\"})")] string fields,
        [Description("Convert values before sending. Supported: \"markdown\" (converts Markdown to HTML)")] string? format = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return McpResultBuilder.ToError("Usage: twig_patch requires a non-empty JSON object of field name → value pairs.");

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToError($"Unknown format '{format}'. Supported formats: markdown");

        // Parse JSON into field dictionary (AOT-safe via source-generated context)
        Dictionary<string, string>? fieldMap;
        try
        {
            fieldMap = JsonSerializer.Deserialize(fields, TwigJsonContext.Default.DictionaryStringString);
        }
        catch (JsonException ex)
        {
            return McpResultBuilder.ToError($"Invalid JSON: {ex.Message}");
        }

        if (fieldMap is null || fieldMap.Count == 0)
            return McpResultBuilder.ToError("JSON must be a non-empty object with field name → value pairs.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

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

        // Fetch remote and PATCH with conflict retry
        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            await ConflictRetryHelper.PatchWithRetryAsync(ctx.AdoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return McpResultBuilder.ToError(ex.Message);
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

        return McpResultBuilder.FormatPatch(updated, fieldChanges);
    }

    [McpServerTool(Name = "twig_note"), Description("Add a comment/note to the active work item")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add as a comment")] string text,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return McpResultBuilder.ToError("Usage: twig_note requires non-empty text.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        var resolved = await ctx.ActiveItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

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

        return McpResultBuilder.FormatNoteAdded(item.Id, item.Title, isPending);
    }

    [McpServerTool(Name = "twig_delete"), Description("Permanently delete a work item from Azure DevOps (two-phase: first call returns confirmation prompt, second call with confirmed=true executes deletion)")]
    public async Task<CallToolResult> Delete(
        [Description("The work item ID to delete")] int id,
        [Description("Set to true to confirm and execute the deletion. Omit or set false for the confirmation prompt.")] bool confirmed = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (id <= 0)
            return McpResultBuilder.ToError("Usage: twig_delete requires a positive work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        // Resolve item from cache or ADO
        var (item, fetchError) = await ctx.FetchWithFallbackAsync(id, ct);
        if (item is null)
            return McpResultBuilder.ToError(fetchError ?? $"Work item #{id} not found.");

        // Seed guard
        if (item.IsSeed)
            return McpResultBuilder.ToError($"#{id} is a seed. Use 'twig seed discard {id}' instead.");

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
            return McpResultBuilder.ToError(ex.Message);
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

            return McpResultBuilder.ToError(
                $"Cannot delete #{id} '{freshItem.Title}' — it has {linkCount} link(s): {string.Join(", ", parts)}. " +
                "Remove all links before deleting. Consider 'twig_state Closed' instead — it preserves history and is reversible.");
        }

        // Phase 1: Confirmation prompt
        if (!confirmed)
            return McpResultBuilder.FormatDeleteConfirmation(freshItem);

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
            return McpResultBuilder.ToError($"Delete failed: {ex.Message}");
        }

        // Cache cleanup
        await ctx.WorkItemRepo.DeleteByIdAsync(id, ct);
        await ctx.PendingChangeStore.ClearChangesAsync(id, ct);

        // Prompt state refresh — best-effort
        try { await ctx.PromptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return McpResultBuilder.FormatDeleted(id, freshItem.Title);
    }

    [McpServerTool(Name = "twig_sync"),Description("Flush pending local changes to ADO then refresh the local cache from ADO")]
    public async Task<CallToolResult> Sync(
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return McpResultBuilder.ToError(err!);

        // Phase 1 — Push:flush all pending changes to ADO
        var flushSummary = await ctx.Flusher.FlushAllAsync(ct);

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

        return McpResultBuilder.FormatFlushSummary(flushSummary);
    }
}