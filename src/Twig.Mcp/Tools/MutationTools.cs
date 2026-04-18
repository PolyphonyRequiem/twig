using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for mutations: twig_state, twig_update, twig_note, twig_discard, twig_sync.
/// </summary>
[McpServerToolType]
public sealed class MutationTools(
    ActiveItemResolver activeItemResolver,
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    IPromptStateWriter promptStateWriter,
    McpPendingChangeFlusher flusher,
    SyncCoordinator syncCoordinator)
{
    [McpServerTool(Name = "twig_state"), Description("Change the state of the active work item")]
    public async Task<CallToolResult> State(
        [Description("Target state name (full or partial, case-insensitive)")] string stateName,
        [Description("Set to true to proceed with backward or cut (remove-type) transitions without interactive confirmation")] bool force = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stateName))
            return McpResultBuilder.ToError("Usage: twig_state requires a target state name (e.g. Active, Closed, Resolved).");

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig_set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

        var processConfig = processConfigProvider.GetConfiguration();
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

        if (transition.RequiresConfirmation && !force)
            return McpResultBuilder.ToError(
                $"Transition from '{item.State}' to '{newState}' requires confirmation (kind: {transition.Kind}). Retry with force: true to proceed.");

        WorkItem remote;
        try
        {
            remote = await adoService.FetchAsync(item.Id, ct);
            var changes = new[] { new FieldChange("System.State", item.State, newState) };
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return McpResultBuilder.ToError(ex.Message);
        }

        try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Resync cache — best-effort, non-fatal
        WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
        }

        try { await promptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return McpResultBuilder.FormatStateChange(updated, previousState);
    }

    [McpServerTool(Name = "twig_update"), Description("Update a field on the active work item and push to ADO")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. System.Title, System.Description, Microsoft.VSTS.Scheduling.StoryPoints)")] string field,
        [Description("New field value")] string value,
        [Description("Set to 'markdown' to convert the value from Markdown to HTML before storing (useful for System.Description)")] string? format = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(field) || value is null)
            return McpResultBuilder.ToError("Usage: twig_update requires a field name and value.");

        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToError($"Unknown format '{format}'. Supported formats: markdown");

        var effectiveValue = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            ? MarkdownConverter.ToHtml(value)
            : value;

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
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
            remote = await adoService.FetchAsync(item.Id, ct);
            var changes = new[] { new FieldChange(field, null, effectiveValue) };
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoException ex)
        {
            return McpResultBuilder.ToError(ex.Message);
        }

        try { await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        // Resync cache — best-effort, non-fatal
        WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
        }

        try { await promptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return McpResultBuilder.FormatFieldUpdate(updated, field, value);
    }

    [McpServerTool(Name = "twig_note"), Description("Add a comment/note to the active work item")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add as a comment")] string text,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return McpResultBuilder.ToError("Usage: twig_note requires non-empty text.");

        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
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
            await adoService.AddCommentAsync(item.Id, text, ct);
            isPending = false;

            await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await pendingChangeStore.AddChangeAsync(item.Id, "note", fieldName: null, oldValue: null, newValue: text, ct);
            isPending = true;
        }

        // Resync cache — best-effort, only on successful push
        if (!isPending)
        {
            try
            {
                var updated = await adoService.FetchAsync(item.Id, ct);
                await workItemRepo.SaveAsync(updated, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best-effort
            }
        }

        await promptStateWriter.WritePromptStateAsync();

        return McpResultBuilder.FormatNoteAdded(item.Id, item.Title, isPending);
    }

    [McpServerTool(Name = "twig_discard"), Description("Discard pending changes for a work item")]
    public async Task<CallToolResult> Discard(
        [Description("Work item ID to discard changes for (defaults to active item)")] int? id = null,
        CancellationToken ct = default)
    {
        // Resolve target: explicit ID or active item
        WorkItem cached;
        if (id.HasValue)
        {
            var found = await workItemRepo.GetByIdAsync(id.Value, ct);
            if (found is null)
                return McpResultBuilder.ToError($"Work item #{id.Value} not found in cache.");
            cached = found;
        }
        else
        {
            var resolved = await activeItemResolver.GetActiveItemAsync(ct);
            if (resolved is ActiveItemResult.NoContext)
                return McpResultBuilder.ToError("No active work item. Use twig_set to set context, or pass an explicit id.");
            if (resolved is ActiveItemResult.Unreachable u)
                return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

            cached = resolved is ActiveItemResult.Found f
                ? f.WorkItem
                : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;
        }

        // Get change summary — return early if nothing to discard
        var (notes, fieldEdits) = await pendingChangeStore.GetChangeSummaryAsync(cached.Id, ct);
        if (notes == 0 && fieldEdits == 0)
            return McpResultBuilder.FormatDiscardNone(cached.Id, cached.Title);

        // Clear pending changes and dirty flag
        await pendingChangeStore.ClearChangesAsync(cached.Id, ct);
        await workItemRepo.ClearDirtyFlagAsync(cached.Id, ct);

        // Update prompt state — best-effort
        try { await promptStateWriter.WritePromptStateAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }

        return McpResultBuilder.FormatDiscard(cached.Id, cached.Title, notes, fieldEdits);
    }

    [McpServerTool(Name = "twig_sync"), Description("Flush pending local changes to ADO then refresh the local cache from ADO")]
    public async Task<CallToolResult> Sync(CancellationToken ct = default)
    {
        // Phase 1 — Push: flush all pending changes to ADO
        var flushSummary = await flusher.FlushAllAsync(ct);

        // Phase 2 — Pull: sync active item context from ADO
        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.Found or ActiveItemResult.FetchedFromAdo)
        {
            var item = resolved is ActiveItemResult.Found f
                ? f.WorkItem
                : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

            var idsToSync = new List<int> { item.Id };

            if (item.ParentId.HasValue)
            {
                var chain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
                idsToSync.AddRange(chain.Select(p => p.Id));
            }

            var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
            idsToSync.AddRange(children.Select(c => c.Id));

            try
            {
                await syncCoordinator.SyncItemSetAsync(idsToSync.Distinct().ToList(), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        await promptStateWriter.WritePromptStateAsync();

        return McpResultBuilder.FormatFlushSummary(flushSummary);
    }
}
