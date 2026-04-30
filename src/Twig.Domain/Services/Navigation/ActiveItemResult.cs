using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Navigation;

/// <summary>Active item was found in local cache.</summary>
public sealed record Found(WorkItem WorkItem);

/// <summary>No active work item context is configured.</summary>
public sealed record ActiveNoContext;

/// <summary>Active item was fetched from Azure DevOps.</summary>
public sealed record FetchedFromAdo(WorkItem WorkItem);

/// <summary>Active item could not be reached.</summary>
public sealed record ActiveUnreachable(int Id, string Reason);

/// <summary>
/// Discriminated union representing the outcome of active item resolution.
/// Commands pattern-match on this to decide display or error behavior.
/// </summary>
public union ActiveItemResult(Found, ActiveNoContext, FetchedFromAdo, ActiveUnreachable);
