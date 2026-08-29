using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Plan;
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
public sealed class StateTransitionWorkflow
{
    private const string SystemStateField = "System.State";

    /// <summary>
    /// Plan-operation id for the synthetic single-field batch this workflow hands the shared
    /// process-rule evaluator. The batch is never written, previewed, or journalled — it is
    /// only the evaluator's input shape — but the id must be stable and self-describing for
    /// anything that ends up echoing it.
    /// </summary>
    private const string StateTransitionGateOperationId = "state-transition-gate";

    private readonly IWorkItemRepository workItemRepo;
    private readonly IAdoWorkItemService adoService;
    private readonly IPendingChangeStore pendingChangeStore;
    private readonly IProcessConfigurationProvider processConfigProvider;
    private readonly ParentStatePropagationService? parentPropagation;
    private readonly IPromptStateWriter? promptStateWriter;
    private readonly IProcessRuleProvider? processRuleProvider;

    public StateTransitionWorkflow(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IPendingChangeStore pendingChangeStore,
        IProcessConfigurationProvider processConfigProvider,
        ParentStatePropagationService? parentPropagation = null,
        IPromptStateWriter? promptStateWriter = null)
        : this(
            workItemRepo,
            adoService,
            pendingChangeStore,
            processConfigProvider,
            parentPropagation,
            promptStateWriter,
            processRuleProvider: null)
    {
    }

    internal StateTransitionWorkflow(
        IWorkItemRepository workItemRepo,
        IAdoWorkItemService adoService,
        IPendingChangeStore pendingChangeStore,
        IProcessConfigurationProvider processConfigProvider,
        ParentStatePropagationService? parentPropagation,
        IPromptStateWriter? promptStateWriter,
        IProcessRuleProvider? processRuleProvider)
    {
        this.workItemRepo = workItemRepo;
        this.adoService = adoService;
        this.pendingChangeStore = pendingChangeStore;
        this.processConfigProvider = processConfigProvider;
        this.parentPropagation = parentPropagation;
        this.promptStateWriter = promptStateWriter;
        this.processRuleProvider = processRuleProvider;
    }

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

        Func<WorkItem, string, IReadOnlyList<FieldChange>>? dependentFieldPlanner = null;
        if (processRuleProvider is not null)
        {
            // Refuse a transition the process itself will not accept, BEFORE touching the wire.
            // `twig state` writes System.State and nothing else, so a caller-owned gate field
            // (an authored `when State = X -> makeRequired Y` with no paired supplier) can never
            // be satisfied through this command; emitting the PATCH would spend a round trip to
            // be told so, and under bypassRules could walk straight past the gate instead.
            // Reuses the plan surface's evaluator so the AB#803 supplied-requirement semantics
            // have exactly one implementation.
            var gate = new PlanProcessRuleGate(processRuleProvider);
            var gateOutcome = await gate.EvaluateAsync(
                new BatchOperation
                {
                    Id = StateTransitionGateOperationId,
                    WorkItemId = item.Id,
                    ExpectedRevision = expectedRevision,
                    Fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        [SystemStateField] = newState,
                    },
                },
                item,
                ct);

            if (gateOutcome.IsRefused)
            {
                return new StateTransitionOutcome.RequiredFieldsMissing(
                    item.Id,
                    item.Type.Value,
                    newState,
                    gateOutcome.Fields ?? []);
            }

            var rules = await processRuleProvider.GetRulesAsync(item.Type.Value, ct);
            dependentFieldPlanner = (currentItem, targetState) =>
                DependentFieldReconciler.GetSafeClears(
                    rules,
                    currentItem.State,
                    targetState,
                    currentItem.Fields);
        }

        var execution = await StateTransitionExecutor.ExecuteAsync(
            adoService,
            item,
            newState,
            typeConfig,
            expectedRevision,
            ct,
            dependentFieldPlanner);

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
