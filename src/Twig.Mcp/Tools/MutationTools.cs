using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Content;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for mutations: twig.state, twig.update, twig.note, twig.sync.
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
    [McpServerTool(Name = "twig.state"), Description("Change the state of the active work item")]
    public async Task<CallToolResult> State(
        [Description("Target state name (full or partial, case-insensitive)")] string stateName,
        [Description("Set to true to proceed with backward or cut (remove-type) transitions without interactive confirmation")] bool force = false,
        CancellationToken ct = default)
    {
        // 1. Validate input
        if (string.IsNullOrWhiteSpace(stateName))
            return McpResultBuilder.ToError("Usage: twig.state requires a target state name (e.g. Active, Closed, Resolved).");

        // 2. Resolve active item
        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig.set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

        // 3. Get process config
        var processConfig = processConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return McpResultBuilder.ToError($"No process configuration found for type '{item.Type}'.");

        // 4. Resolve state name
        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
            return McpResultBuilder.ToError(resolveResult.Error);

        var newState = resolveResult.Value;
        var previousState = item.State;

        // 5. Already in target state
        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToResult($"Already in state '{newState}'.");

        // 6. Evaluate transition
        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);

        if (!transition.IsAllowed)
            return McpResultBuilder.ToError($"Transition from '{item.State}' to '{newState}' is not allowed.");

        if (transition.RequiresConfirmation && !force)
            return McpResultBuilder.ToError(
                $"Transition from '{item.State}' to '{newState}' requires confirmation (kind: {transition.Kind}). Retry with force: true to proceed.");

        // 7. Push to ADO
        var remote = await adoService.FetchAsync(item.Id, ct);
        var changes = new[] { new FieldChange("System.State", item.State, newState) };
        await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);

        // 8. Auto-push pending notes
        await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);

        // 9. Resync cache (best-effort, non-fatal)
        Domain.Aggregates.WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — the ADO transition already succeeded.
            // Fall back to the original item for the response.
            updated = item;
        }

        // 10. Write prompt state
        await promptStateWriter.WritePromptStateAsync();

        // 11. Return success
        return McpResultBuilder.FormatStateChange(updated, previousState);
    }

    [McpServerTool(Name = "twig.update"), Description("Update a field on the active work item and push to ADO")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. System.Title, System.Description, Microsoft.VSTS.Scheduling.StoryPoints)")] string field,
        [Description("New field value")] string value,
        [Description("Set to 'markdown' to convert the value from Markdown to HTML before storing (useful for System.Description)")] string? format = null,
        CancellationToken ct = default)
    {
        // 1. Validate inputs
        if (string.IsNullOrWhiteSpace(field) || value is null)
            return McpResultBuilder.ToError("Usage: twig.update requires a field name and value.");

        // 2. Validate format
        if (format is not null && !string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase))
            return McpResultBuilder.ToError($"Unknown format '{format}'. Supported formats: markdown");

        // 3. Compute effective value
        var effectiveValue = string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
            ? MarkdownConverter.ToHtml(value)
            : value;

        // 4. Resolve active item
        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig.set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

        // 5. Fetch remote
        var remote = await adoService.FetchAsync(item.Id, ct);

        // 6. Push field change
        var changes = new[] { new FieldChange(field, null, effectiveValue) };
        try
        {
            await ConflictRetryHelper.PatchWithRetryAsync(adoService, item.Id, changes, remote.Revision, ct);
        }
        catch (AdoConflictException)
        {
            return McpResultBuilder.ToError("Concurrency conflict after retry. Use twig.sync to resync and retry.");
        }

        // 7. Auto-push pending notes
        await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);

        // 8. Resync cache
        var updated = await adoService.FetchAsync(item.Id, ct);
        await workItemRepo.SaveAsync(updated, ct);

        // 9. Write prompt state
        await promptStateWriter.WritePromptStateAsync();

        // 10. Return success
        return McpResultBuilder.FormatFieldUpdate(updated, field, value);
    }

    [McpServerTool(Name = "twig.note"), Description("Add a comment/note to the active work item")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add as a comment")] string text,
        CancellationToken ct = default)
    {
        // 1. Validate input
        if (string.IsNullOrWhiteSpace(text))
            return McpResultBuilder.ToError("Usage: twig.note requires non-empty text.");

        // 2. Resolve active item
        var resolved = await activeItemResolver.GetActiveItemAsync(ct);
        if (resolved is ActiveItemResult.NoContext)
            return McpResultBuilder.ToError("No active work item. Use twig.set to set context.");
        if (resolved is ActiveItemResult.Unreachable u)
            return McpResultBuilder.ToError($"Work item #{u.Id} not found in cache.");

        var item = resolved is ActiveItemResult.Found f
            ? f.WorkItem
            : ((ActiveItemResult.FetchedFromAdo)resolved).WorkItem;

        // 3. Push comment to ADO (fall back to local staging on failure)
        bool isPending;
        try
        {
            await adoService.AddCommentAsync(item.Id, text, ct);
            isPending = false;

            // 4. Clear any previously staged notes (only on successful push)
            await pendingChangeStore.ClearChangesByTypeAsync(item.Id, "note", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ADO unreachable — stage locally
            await pendingChangeStore.AddChangeAsync(item.Id, "note", fieldName: null, oldValue: null, newValue: text, ct);
            isPending = true;
        }

        // 5. Resync cache (best-effort, only on successful push)
        if (!isPending)
        {
            try
            {
                var updated = await adoService.FetchAsync(item.Id, ct);
                await workItemRepo.SaveAsync(updated, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort — the ADO comment already succeeded.
            }
        }

        // 6. Write prompt state
        await promptStateWriter.WritePromptStateAsync();

        // 7. Return success
        return McpResultBuilder.FormatNoteAdded(item.Id, item.Title, isPending);
    }

    [McpServerTool(Name = "twig.sync"), Description("Flush pending local changes to ADO then refresh the local cache from ADO")]
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

        // Write prompt state
        await promptStateWriter.WritePromptStateAsync();

        // Return flush summary
        return McpResultBuilder.FormatFlushSummary(flushSummary);
    }
}
