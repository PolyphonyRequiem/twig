using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated outcome of a published-work-item state transition executed
/// by <see cref="StateTransitionWorkflow"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each adapter (CLI <c>StateCommand</c>, MCP <c>MutationTools.State</c>) pattern-matches
/// this outcome to produce its rendering. The workflow itself is rendering-agnostic.
/// </para>
/// <para>
/// Seed (local-only) state changes never reach this outcome — they remain in the adapter
/// and dispatch directly to <see cref="SeedMutationProvider"/>. Including a seed variant
/// here would force every adapter to carry rendering noise for a path the workflow
/// never executes.
/// </para>
/// </remarks>
public abstract record StateTransitionOutcome
{
    private StateTransitionOutcome() { }

    /// <summary>State name could not be resolved against the type's state graph.</summary>
    public sealed record InvalidStateName(string Error) : StateTransitionOutcome;

    /// <summary>Work item type has no entry in the active process configuration.</summary>
    public sealed record ProcessConfigNotFound(string Type) : StateTransitionOutcome;

    /// <summary>Item is already in the requested state — no transition performed.</summary>
    public sealed record AlreadyInState(string ResolvedState, ResolutionKind ResolutionKind, string Input)
        : StateTransitionOutcome;

    /// <summary>Workflow graph forbids the transition from the current to the target state.</summary>
    public sealed record TransitionNotAllowed(string FromState, string ToState) : StateTransitionOutcome;

    /// <summary>
    /// ADO rejected one PATCH in the transition chain. <see cref="Path"/> is the prefix
    /// successfully traversed; <see cref="FinalState"/> is the last state reached.
    /// <see cref="CacheResyncWarning"/> is set if the best-effort cache resync after the
    /// failed chain also failed.
    /// </summary>
    public sealed record ChainFailed(
        int ItemId,
        IReadOnlyList<string> Path,
        string FinalState,
        string AdoError,
        string? CacheResyncWarning)
        : StateTransitionOutcome;

    /// <summary>
    /// Transition (possibly multi-hop) completed. <paramref name="UpdatedItem"/> reflects the
    /// post-transition ADO state. <paramref name="Warnings"/> captures best-effort failures
    /// (auto-push, resync, prompt-state, parent-prop) so the adapter can surface them without
    /// failing the overall command.
    /// </summary>
    public sealed record Succeeded(
        WorkItem UpdatedItem,
        string PreviousState,
        string NewState,
        ResolutionKind ResolutionKind,
        string Input,
        IReadOnlyList<string> Path,
        ParentPropagationResult? ParentPropagation,
        IReadOnlyList<string> Warnings)
        : StateTransitionOutcome;
}
