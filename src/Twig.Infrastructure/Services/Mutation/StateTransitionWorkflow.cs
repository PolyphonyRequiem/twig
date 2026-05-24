using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Orchestrates a published (non-seed) work-item state transition end-to-end:
/// process-config lookup, name/category resolution, transition validation, optimistic-
/// concurrency chained PATCH, pending-note flush, cache resync, parent propagation.
/// </summary>
/// <remarks>
/// <para>
/// Both the CLI <c>StateCommand</c> and the MCP <c>twig_state</c> tool route through this
/// workflow so the orchestration cannot drift between them. Adapter responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Parse arguments and resolve the target <see cref="WorkItem"/>.</item>
///   <item>Branch local-only seed mutations to <see cref="SeedMutationProvider"/> directly
///         — the workflow only runs against published items.</item>
///   <item>(CLI only) Perform interactive conflict detection before calling
///         the workflow. The workflow assumes the caller's <c>expectedRevision</c>
///         reflects an acceptable baseline.</item>
///   <item>Render the resulting <see cref="StateTransitionOutcome"/> for the user.</item>
/// </list>
/// <para>
/// Best-effort side-effects (auto-note-flush, cache resync, parent propagation, prompt-state
/// write) never fail the workflow — they accumulate into <see cref="StateTransitionOutcome.Succeeded.Warnings"/>
/// so adapters can surface them without changing exit codes.
/// </para>
/// </remarks>
public sealed class StateTransitionWorkflow(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore,
    IProcessConfigurationProvider processConfigProvider,
    ParentStatePropagationService? parentPropagation = null,
    IPromptStateWriter? promptStateWriter = null)
{
    /// <summary>
    /// Pure pre-flight validation. Returns the terminal outcome (InvalidStateName,
    /// ProcessConfigNotFound, AlreadyInState, TransitionNotAllowed) if the transition
    /// cannot proceed; returns <c>null</c> if the caller may continue to
    /// <see cref="ExecuteAsync(WorkItem, string, int, CancellationToken)"/>. No side effects.
    /// </summary>
    public StateTransitionOutcome? Validate(WorkItem item, string stateName)
    {
        var processConfig = processConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return new StateTransitionOutcome.ProcessConfigNotFound(item.Type.Value);

        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
            return new StateTransitionOutcome.InvalidStateName(resolveResult.Error);

        var resolution = resolveResult.Value;
        var newState = resolution.ResolvedName;
        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
            return new StateTransitionOutcome.AlreadyInState(newState, resolution.Kind, stateName);

        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);
        if (!transition.IsAllowed)
            return new StateTransitionOutcome.TransitionNotAllowed(item.State, newState);

        return null;
    }

    /// <summary>
    /// Executes the state transition. <paramref name="expectedRevision"/> is the ADO revision
    /// the caller has acknowledged (typically the result of a fresh fetch + conflict check).
    /// </summary>
    public async Task<StateTransitionOutcome> ExecuteAsync(
        WorkItem item,
        string stateName,
        int expectedRevision,
        CancellationToken ct = default)
    {
        var processConfig = processConfigProvider.GetConfiguration();
        if (!processConfig.TypeConfigs.TryGetValue(item.Type, out var typeConfig))
            return new StateTransitionOutcome.ProcessConfigNotFound(item.Type.Value);

        var resolveResult = StateResolver.ResolveByName(stateName, typeConfig.StateEntries);
        if (!resolveResult.IsSuccess)
            return new StateTransitionOutcome.InvalidStateName(resolveResult.Error);

        var resolution = resolveResult.Value;
        var newState = resolution.ResolvedName;
        var previousState = item.State;

        if (string.Equals(item.State, newState, StringComparison.OrdinalIgnoreCase))
            return new StateTransitionOutcome.AlreadyInState(newState, resolution.Kind, stateName);

        var transition = StateTransitionService.Evaluate(processConfig, item.Type, item.State, newState);
        if (!transition.IsAllowed)
            return new StateTransitionOutcome.TransitionNotAllowed(item.State, newState);

        var execution = await StateTransitionExecutor.ExecuteAsync(
            adoService, item, newState, typeConfig, expectedRevision, ct);

        if (!execution.IsSuccess)
        {
            string? resyncWarning = null;
            try
            {
                var partial = await adoService.FetchAsync(item.Id, ct);
                await workItemRepo.SaveAsync(partial, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resyncWarning = $"cache may be stale after partial state chain ({ex.Message})";
            }

            return new StateTransitionOutcome.ChainFailed(
                item.Id, execution.Path, execution.FinalState, execution.ErrorMessage!, resyncWarning);
        }

        var warnings = new List<string>();

        try
        {
            await AutoPushNotesHelper.PushAndClearAsync(item.Id, pendingChangeStore, adoService);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add($"auto-push notes failed: {ex.Message}");
        }

        WorkItem updated;
        try
        {
            updated = await adoService.FetchAsync(item.Id, ct);
            await workItemRepo.SaveAsync(updated, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            updated = item;
            warnings.Add($"State changed to '{newState}' but cache may be stale — run 'twig sync' to resync ({ex.Message})");
        }

        ParentPropagationResult? propagation = null;
        if (parentPropagation is not null)
        {
            var newCategory = StateCategoryResolver.Resolve(newState, typeConfig.StateEntries);
            if (newCategory == StateCategory.InProgress)
                propagation = await parentPropagation.TryPropagateToParentAsync(updated, StateCategory.InProgress, ct);
        }

        if (promptStateWriter is not null)
        {
            try
            {
                await promptStateWriter.WritePromptStateAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"prompt-state write failed: {ex.Message}");
            }
        }

        return new StateTransitionOutcome.Succeeded(
            updated, previousState, newState, resolution.Kind, stateName, execution.Path, propagation, warnings);
    }
}
