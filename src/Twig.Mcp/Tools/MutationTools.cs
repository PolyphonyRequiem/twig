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
    [McpServerTool(Name = "twig_state"),Description("Change the state of a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
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

        var (item, resolveError) = await WorkItemResolver.ResolveWorkItemAsync(ctx, id, ct);
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

        // Pre-flight validation (pure, no side effects). Bails on bad input
        // before fetching remote.
        var preflight = ctx.StateTransitionWorkflow.Validate(item, stateName);
        if (preflight is not null)
            return await RenderOutcomeAsync(ctx, item, preflight, verbose, ct);

        WorkItem remote;
        StateTransitionOutcome outcome;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            outcome = await ctx.StateTransitionWorkflow.ExecuteAsync(item, stateName, remote.Revision, ct);
        }
        catch (AdoBadRequestException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.AdoValidationFailed,
                ex.Message,
                ctx,
                ct,
                new Dictionary<string, string>
                {
                    ["remediation"] = "Update System.State and any dependent fields together with twig_patch.",
                });
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        return await RenderOutcomeAsync(ctx, item, outcome, verbose, ct);
    }

    private static async Task<CallToolResult> RenderOutcomeAsync(
        WorkspaceContext ctx, WorkItem item, StateTransitionOutcome outcome, bool verbose, CancellationToken ct)
    {
        switch (outcome)
        {
            case StateTransitionOutcome.InvalidStateName x:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidStateTransition, x.Error, ctx, ct);

            case StateTransitionOutcome.ProcessConfigNotFound x:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.CacheStale, $"No process configuration found for type '{x.Type}'.", ctx, ct);

            case StateTransitionOutcome.AlreadyInState x:
                return await EnvelopeBuilder.SuccessAsync(ctx, w =>
                {
                    var msg = x.ResolutionKind == ResolutionKind.Category
                        ? $"Already in state '{x.ResolvedState}' (category '{x.Input}')."
                        : $"Already in state '{x.ResolvedState}'.";
                    w.WriteString("message", msg);
                    w.WriteString("state", x.ResolvedState);
                    if (x.ResolutionKind == ResolutionKind.Category)
                        w.WriteString("resolved_from_category", x.Input);
                }, verbose, ct);

            case StateTransitionOutcome.TransitionNotAllowed x:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidStateTransition, $"Transition from '{x.FromState}' to '{x.ToState}' is not allowed.", ctx, ct);

            case StateTransitionOutcome.ChainFailed x:
                var pathRendered = string.Join(" → ", x.Path);
                var failureMsg = x.Path.Count > 1
                    ? $"chain stopped at '{x.FinalState}'. Reached: {pathRendered}. ADO: {x.AdoError}"
                    : $"transition rejected. ADO: {x.AdoError}";
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidStateTransition, failureMsg, ctx, ct);

            case StateTransitionOutcome.Succeeded x:
                return await EnvelopeBuilder.WrapAsync(ctx,
                    McpResultBuilder.FormatStateChange(x.UpdatedItem, x.PreviousState, x.Path), verbose, ct);

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled StateTransitionOutcome: {outcome.GetType().Name}");
        }
    }

    [McpServerTool(Name = "twig_update"), Description("Update a field on a work item and push to ADO. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Update(
        [Description("Field reference name (e.g. System.Title, System.Description, Microsoft.VSTS.Scheduling.StoryPoints)")] string field,
        [Description("New field value")] string value,
        [Description("Convert the input value before sending to ADO. Supported: \"markdown\" (force-convert), \"raw\" (pass through unchanged). Default: auto — converts only when the destination field is HTML-typed in ADO (e.g. System.Description).")] string? format = null,
        [Description("When true, append the value to the existing field content instead of replacing it")] bool append = false,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(field) || value is null)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_update requires a field name and value.");

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, formatError);

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var resolution = await HtmlFieldFormatter.ResolveAsync(field, value, format, ctx.FieldDefinitionStore, onMissingFieldDef: null, ct);
        var effectiveValue = resolution.EffectiveValue;

        var (item, resolveError) = await WorkItemResolver.ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Seed routing: local-only mutation, no ADO interaction.
        if (item.IsSeed)
        {
            if (append)
            {
                item.Fields.TryGetValue(field, out var existingValue);
                effectiveValue = FieldAppender.Append(existingValue, effectiveValue, asHtml: resolution.IsHtml);
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
        FieldUpdateOutcome outcome;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
            outcome = await ctx.FieldUpdateWorkflow.ExecuteAsync(
                item, remote, field, effectiveValue, resolution.IsHtml, append, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        switch (outcome)
        {
            case FieldUpdateOutcome.ConflictAfterRetry:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, "Concurrency conflict after retry. Run 'twig sync' and retry.", ctx, ct);

            case FieldUpdateOutcome.Succeeded x:
                return await EnvelopeBuilder.WrapAsync(ctx,
                    McpResultBuilder.FormatFieldUpdate(x.UpdatedItem, field, value), verbose, ct);

            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled FieldUpdateOutcome: {outcome.GetType().Name}");
        }
    }

    [McpServerTool(Name = "twig_patch"), Description("Atomically patch multiple fields on a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Patch(
        [Description("JSON object with field reference name → value pairs (e.g. {\"System.Title\":\"New\",\"System.Description\":\"Desc\"})")] string fields,
        [Description("Convert values before sending. Supported: \"markdown\" (force-convert all fields), \"raw\" (pass through unchanged). Default: auto — converts each field individually when its ADO type is HTML.")] string? format = null,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_patch requires a non-empty JSON object of field name → value pairs.");

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, formatError);

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

        var (item, resolveError) = await WorkItemResolver.ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        // Build FieldChange[] with per-field conversion (auto-detected via field type)
        var changes = new List<FieldChange>(fieldMap.Count);
        var fieldChanges = new Dictionary<string, (string? OldValue, string? NewValue)>(fieldMap.Count);
        foreach (var (key, value) in fieldMap)
        {
            var fieldResolution = await HtmlFieldFormatter.ResolveAsync(key, value, format, ctx.FieldDefinitionStore, onMissingFieldDef: null, ct);
            changes.Add(new FieldChange(key, null, fieldResolution.EffectiveValue));
            fieldChanges[key] = (null, fieldResolution.EffectiveValue);
        }

        // Seed path: workflow handles seed routing.
        if (item.IsSeed)
        {
            var seedOutcome = await ctx.PatchWorkflow.ExecuteAsync(item, changes, remote: null, ct);
            return seedOutcome switch
            {
                PatchOutcome.SeedPatched =>
                    await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatPatch(item, fieldChanges), verbose, ct),
                PatchOutcome.SeedFieldRejected r =>
                    await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, $"Field '{r.FieldName}' failed: {r.Reason}", ctx, ct),
                _ => throw new System.Diagnostics.UnreachableException($"Unhandled seed PatchOutcome: {seedOutcome.GetType().Name}"),
            };
        }

        // Non-seed: fetch remote, then call workflow.
        WorkItem remote;
        try
        {
            remote = await ctx.AdoService.FetchAsync(item.Id, ct);
        }
        catch (AdoException ex)
        {
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, ex.Message, ctx, ct);
        }

        var outcome = await ctx.PatchWorkflow.ExecuteAsync(item, changes, remote, ct);
        return outcome switch
        {
            PatchOutcome.Patched p =>
                await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatPatch(p.UpdatedItem, fieldChanges), verbose, ct),
            PatchOutcome.ConflictAfterRetry =>
                await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, "Concurrency conflict after retry. Run twig_sync and retry.", ctx, ct),
            PatchOutcome.AdoUnreachable a =>
                await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, a.Reason, ctx, ct),
            _ => throw new System.Diagnostics.UnreachableException($"Unhandled PatchOutcome: {outcome.GetType().Name}"),
        };
    }

    [McpServerTool(Name = "twig_note"), Description("Add a comment/note to a work item. Operates on the active work item by default, or specify id to target a specific item without changing context.")]
    public async Task<CallToolResult> Note(
        [Description("Note text to add as a comment")] string text,
        [Description("Work item ID to operate on. When omitted, uses the active work item. When provided, the active context is not changed.")] int? id = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("Convert the note text before sending. Supported: \"markdown\" (default) converts Markdown to HTML; \"raw\" sends pre-rendered HTML or plain text unchanged.")] string? format = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_note requires non-empty text.");

        var formatError = HtmlFieldFormatter.ValidateFormat(format);
        if (formatError is not null)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, formatError);

        if (!resolver.TryResolve(workspace, out var ctx, out var err)) return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var (item, resolveError) = await WorkItemResolver.ResolveWorkItemAsync(ctx, id, ct);
        if (item is null) return resolveError!;

        var commentResolution = HtmlFieldFormatter.ResolveComment(text, format);

        var outcome = await ctx.NoteWorkflow.ExecuteAsync(item, commentResolution.EffectiveValue, commentResolution.IsHtml, ct);

        bool isPending = outcome switch
        {
            NoteOutcome.Pushed => false,
            NoteOutcome.Staged => true,
            _ => throw new System.Diagnostics.UnreachableException($"Unhandled NoteOutcome: {outcome.GetType().Name}"),
        };

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

        // Resolve item from cache or ADO (for seed guard + early not-found error)
        var (item, fetchError) = await ctx.FetchWithFallbackAsync(id, ct);
        if (item is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, fetchError ?? $"Work item #{id} not found.", ctx, ct);

        // Seed guard
        if (item.IsSeed)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput, $"#{id} is a seed. Use 'twig seed discard {id}' instead.", ctx, ct);

        // Workflow: fresh fetch + link guard
        var preparation = await ctx.DeleteWorkflow.PrepareAsync(id, ct);
        WorkItem freshItem;
        switch (preparation)
        {
            case DeletePreparation.FetchFailed f:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, f.Reason, ctx, ct);
            case DeletePreparation.BlockedByLinks b:
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                    $"Cannot delete #{id} '{b.FreshItem.Title}' — it has {b.TotalLinkCount} link(s): {b.LinkSummary}. " +
                    "Remove all links before deleting. Consider 'twig_state Closed' instead — it preserves history and is reversible.",
                    ctx, ct);
            case DeletePreparation.Ready r:
                freshItem = r.FreshItem;
                break;
            default:
                throw new System.Diagnostics.UnreachableException($"Unhandled DeletePreparation: {preparation.GetType().Name}");
        }

        // Phase 1: Confirmation prompt
        if (!confirmed)
            return await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDeleteConfirmation(freshItem), verbose, ct);

        // Phase 2: Workflow execution (audit + delete + cache cleanup + prompt-state)
        var outcome = await ctx.DeleteWorkflow.ExecuteAsync(freshItem, ct);
        return outcome switch
        {
            DeleteOutcome.AdoFailed f =>
                await EnvelopeBuilder.ErrorAsync(McpErrorCode.AdoUnreachable, $"Delete failed: {f.Reason}", ctx, ct),
            DeleteOutcome.Deleted =>
                await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDeleted(id, freshItem.Title), verbose, ct),
            _ => throw new System.Diagnostics.UnreachableException($"Unhandled DeleteOutcome: {outcome.GetType().Name}"),
        };
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

        var outcome = await ctx.DiscardWorkflow.ExecuteAsync(item, ct);

        return outcome switch
        {
            DiscardOutcome.NoChanges =>
                await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDiscardedNone(itemId, item.Title), verbose, ct),
            DiscardOutcome.PhantomDirtyCleared =>
                await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDiscardedNone(itemId, item.Title), verbose, ct),
            DiscardOutcome.Discarded x =>
                await EnvelopeBuilder.WrapAsync(ctx, McpResultBuilder.FormatDiscarded(itemId, x.NotesCount, x.FieldEditsCount), verbose, ct),
            _ => throw new System.Diagnostics.UnreachableException($"Unhandled DiscardOutcome: {outcome.GetType().Name}"),
        };
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
        if (resolved is Found or FetchedFromAdo)
        {
            var item = resolved switch
            {
                Found f => f.WorkItem,
                FetchedFromAdo a => a.WorkItem,
                _ => throw new InvalidOperationException("Unexpected active item result"),
            };

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
