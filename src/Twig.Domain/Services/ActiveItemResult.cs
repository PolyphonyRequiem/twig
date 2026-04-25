using Twig.Domain.Aggregates;

namespace Twig.Domain.Services;

/// <summary>
/// Discriminated union representing the outcome of active item resolution.
/// Commands pattern-match on this to decide display or error behavior.
/// </summary>
public abstract record ActiveItemResult
{
    private ActiveItemResult() { }

    public sealed record Found(WorkItem WorkItem) : ActiveItemResult;
    public sealed record NoContext : ActiveItemResult;
    public sealed record FetchedFromAdo(WorkItem WorkItem) : ActiveItemResult;
    public sealed record Unreachable(int Id, string Reason) : ActiveItemResult;
}
