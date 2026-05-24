using System.Collections.Generic;
using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated-union results produced by <c>DeleteWorkflow</c>.
/// </summary>
/// <remarks>
/// <para>
/// Delete is a two-phase operation: callers first invoke
/// <c>PrepareAsync</c> to fetch the fresh item and check the link
/// guard, then (after their own confirmation UI) invoke
/// <c>ExecuteAsync</c> to perform the destructive work.
/// </para>
/// <para>
/// The seed guard is the caller's responsibility (both adapters
/// already do their own seed-routed error formatting).
/// </para>
/// </remarks>
public abstract record DeletePreparation
{
    private DeletePreparation() { }

    /// <summary>Item could not be fetched from ADO.</summary>
    public sealed record FetchFailed(string Reason) : DeletePreparation;

    /// <summary>Item has links (parent, children, or related) — refuse to delete.</summary>
    public sealed record BlockedByLinks(WorkItem FreshItem, int TotalLinkCount, string LinkSummary) : DeletePreparation;

    /// <summary>Item is link-free and ready to be deleted after confirmation.</summary>
    public sealed record Ready(WorkItem FreshItem) : DeletePreparation;
}

/// <summary>
/// Result of <c>DeleteWorkflow.ExecuteAsync</c>.
/// </summary>
public abstract record DeleteOutcome
{
    private DeleteOutcome() { }

    /// <summary>Delete failed against ADO (network / permission / transient).</summary>
    public sealed record AdoFailed(string Reason) : DeleteOutcome;

    /// <summary>Delete succeeded; cache and prompt-state were cleaned up best-effort.</summary>
    public sealed record Deleted(WorkItem FreshItem, IReadOnlyList<string> Warnings) : DeleteOutcome;
}
