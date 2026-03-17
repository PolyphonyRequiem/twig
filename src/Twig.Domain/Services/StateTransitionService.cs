using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Result of evaluating a state transition against a <see cref="ProcessConfiguration"/>.
/// </summary>
public record TransitionResult
{
    public TransitionKind Kind { get; init; }
    public bool IsAllowed { get; init; }
    public bool RequiresConfirmation { get; init; }
    public bool RequiresReason { get; init; }
}

/// <summary>
/// Validates and classifies state transitions against a <see cref="ProcessConfiguration"/>.
/// </summary>
public static class StateTransitionService
{
    /// <summary>
    /// Evaluates a proposed transition from <paramref name="fromState"/> to <paramref name="toState"/>
    /// for the given <paramref name="workItemType"/> within the specified <paramref name="config"/>.
    /// </summary>
    public static TransitionResult Evaluate(
        ProcessConfiguration config,
        WorkItemType workItemType,
        string fromState,
        string toState)
    {
        var kind = config.GetTransitionKind(workItemType, fromState, toState);

        if (kind is null)
        {
            return new TransitionResult
            {
                Kind = TransitionKind.None,
                IsAllowed = false,
                RequiresConfirmation = false,
                RequiresReason = false,
            };
        }

        return kind.Value switch
        {
            TransitionKind.Forward => new TransitionResult
            {
                Kind = TransitionKind.Forward,
                IsAllowed = true,
                RequiresConfirmation = false,
                RequiresReason = false,
            },
            TransitionKind.Backward => new TransitionResult
            {
                Kind = TransitionKind.Backward,
                IsAllowed = true,
                RequiresConfirmation = true,
                RequiresReason = false,
            },
            TransitionKind.Cut => new TransitionResult
            {
                Kind = TransitionKind.Cut,
                IsAllowed = true,
                RequiresConfirmation = true,
                RequiresReason = true,
            },
            // Defensive fallback — TransitionKind.None is never returned by GetTransitionKind,
            // but the compiler requires an exhaustive match and this guards against future enum additions.
            TransitionKind.None or _ => new TransitionResult
            {
                Kind = TransitionKind.None,
                IsAllowed = false,
                RequiresConfirmation = false,
                RequiresReason = false,
            },
        };
    }
}
