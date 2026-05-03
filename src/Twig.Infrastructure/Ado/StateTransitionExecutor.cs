using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;

namespace Twig.Infrastructure.Ado;

/// <summary>
/// Executes a state change against ADO, automatically chaining through intermediate
/// states when the direct transition is rejected by the workflow graph.
/// </summary>
/// <remarks>
/// ADO's per-process transition graph requires process-admin permissions to fetch.
/// Since most twig users are contributors (not admins) we cannot pre-cache the graph
/// and BFS for a shortest path. Instead we use trial-and-error PATCHes, walking the
/// candidate path defined by <see cref="TypeConfig.States"/> (workflow declaration
/// order) which matches the natural transition order of every standard process
/// template (Agile, CMMI, Scrum, Basic).
/// </remarks>
public static class StateTransitionExecutor
{
    /// <summary>
    /// Drives <paramref name="item"/> from its current state to <paramref name="targetState"/>,
    /// chaining through intermediates as necessary. Returns the executed path so the
    /// caller can render it. Best-effort on failure: stops at the first PATCH that ADO
    /// rejects with a transition error and returns the path traversed so far.
    /// </summary>
    /// <param name="adoService">ADO REST client to issue PATCHes through.</param>
    /// <param name="item">The work item, needed for ID and current state.</param>
    /// <param name="targetState">The desired final state.</param>
    /// <param name="typeConfig">Process configuration for the work item type — provides ordered states.</param>
    /// <param name="expectedRevision">The current ADO revision (caller-supplied to honor optimistic concurrency).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<StateTransitionResult> ExecuteAsync(
        IAdoWorkItemService adoService,
        WorkItem item,
        string targetState,
        TypeConfig typeConfig,
        int expectedRevision,
        CancellationToken ct = default)
    {
        var path = new List<string> { item.State };
        var currentState = item.State;
        var currentRevision = expectedRevision;

        // Direct attempt first — covers every single-hop case with no extra round trips.
        var direct = await TryPatchStateAsync(adoService, item.Id, currentState, targetState, currentRevision, ct);
        if (direct.IsSuccess)
        {
            path.Add(targetState);
            return StateTransitionResult.Success(path, targetState, direct.NewRevision);
        }

        if (!direct.IsTransitionError)
            throw direct.UnhandledException!;

        // Direct transition rejected — fall through to chaining. We need both endpoints
        // in the type's state list to compute candidate intermediates; if either is
        // missing (custom state added at the work-item level?) we can't help.
        var intermediates = ComputeIntermediatePath(typeConfig.States, currentState, targetState);
        if (intermediates is null)
        {
            return StateTransitionResult.Failure(
                path,
                currentState,
                currentRevision,
                $"Cannot chain transition from '{currentState}' to '{targetState}': " +
                $"one or both states are not in the type's state list. ADO error: {direct.ErrorMessage}");
        }

        foreach (var intermediate in intermediates)
        {
            var step = await TryPatchStateAsync(adoService, item.Id, currentState, intermediate, currentRevision, ct);
            if (step.IsSuccess)
            {
                currentState = intermediate;
                currentRevision = step.NewRevision;
                path.Add(intermediate);
                continue;
            }

            if (!step.IsTransitionError)
                throw step.UnhandledException!;

            // Transition error on an intermediate — skip it and try the next candidate.
            // (Remember the most recent transition error so we can surface it if the
            //  final retry also fails.)
        }

        // After walking intermediates, retry the original target.
        var final = await TryPatchStateAsync(adoService, item.Id, currentState, targetState, currentRevision, ct);
        if (final.IsSuccess)
        {
            path.Add(targetState);
            return StateTransitionResult.Success(path, targetState, final.NewRevision);
        }

        if (!final.IsTransitionError)
            throw final.UnhandledException!;

        return StateTransitionResult.Failure(
            path,
            currentState,
            currentRevision,
            final.ErrorMessage ?? "transition rejected by ADO");
    }

    private static async Task<PatchAttempt> TryPatchStateAsync(
        IAdoWorkItemService adoService,
        int itemId,
        string fromState,
        string toState,
        int expectedRevision,
        CancellationToken ct)
    {
        var changes = new[] { new FieldChange("System.State", fromState, toState) };
        try
        {
            var newRev = await ConflictRetryHelper.PatchWithRetryAsync(
                adoService, itemId, changes, expectedRevision, ct);
            return PatchAttempt.Success(newRev);
        }
        catch (AdoBadRequestException ex) when (AdoErrorClassifier.IsTransitionError(ex.Message))
        {
            return PatchAttempt.TransitionRejected(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PatchAttempt.Unhandled(ex);
        }
    }

    /// <summary>
    /// Returns the slice of <paramref name="states"/> strictly between the indices of
    /// <paramref name="from"/> and <paramref name="to"/>, in the direction from→to.
    /// Returns <c>null</c> when either endpoint isn't in the list (chaining impossible).
    /// </summary>
    internal static IReadOnlyList<string>? ComputeIntermediatePath(
        IReadOnlyList<string> states,
        string from,
        string to)
    {
        var fromIdx = IndexOf(states, from);
        var toIdx = IndexOf(states, to);
        if (fromIdx < 0 || toIdx < 0) return null;

        if (fromIdx == toIdx) return Array.Empty<string>();

        var result = new List<string>();
        if (fromIdx < toIdx)
        {
            for (var i = fromIdx + 1; i < toIdx; i++)
                result.Add(states[i]);
        }
        else
        {
            for (var i = fromIdx - 1; i > toIdx; i--)
                result.Add(states[i]);
        }
        return result;
    }

    private static int IndexOf(IReadOnlyList<string> states, string name)
    {
        for (var i = 0; i < states.Count; i++)
        {
            if (string.Equals(states[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private readonly record struct PatchAttempt(
        bool IsSuccess,
        int NewRevision,
        bool IsTransitionError,
        string? ErrorMessage,
        Exception? UnhandledException)
    {
        public static PatchAttempt Success(int newRev)
            => new(true, newRev, false, null, null);

        public static PatchAttempt TransitionRejected(string message)
            => new(false, 0, true, message, null);

        public static PatchAttempt Unhandled(Exception ex)
            => new(false, 0, false, ex.Message, ex);
    }
}

/// <summary>
/// Outcome of a (potentially chained) state transition. <see cref="Path"/> always
/// includes the starting state and every intermediate that was successfully reached.
/// On success it also includes the final target.
/// </summary>
public sealed record StateTransitionResult(
    bool IsSuccess,
    IReadOnlyList<string> Path,
    string FinalState,
    int FinalRevision,
    string? ErrorMessage)
{
    /// <summary>Number of PATCHes that succeeded (= <c>Path.Count - 1</c>).</summary>
    public int TransitionCount => Path.Count - 1;

    public static StateTransitionResult Success(IReadOnlyList<string> path, string finalState, int finalRevision)
        => new(true, path, finalState, finalRevision, null);

    public static StateTransitionResult Failure(IReadOnlyList<string> path, string finalState, int finalRevision, string errorMessage)
        => new(false, path, finalState, finalRevision, errorMessage);
}
