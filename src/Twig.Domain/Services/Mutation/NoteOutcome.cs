using System.Collections.Generic;
using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated-union result produced by <c>NoteWorkflow.ExecuteAsync</c>.
/// </summary>
/// <remarks>
/// Adapters (CLI <c>NoteCommand</c>, MCP <c>twig_note</c>) inspect the variant
/// to render the correct success / hint envelope. The two terminal variants are:
/// <list type="bullet">
///   <item><see cref="Pushed"/> — comment was sent to ADO; cache has been resynced.</item>
///   <item><see cref="Staged"/> — comment was queued in the local pending-changes store
///     (either because the item is a seed without an ADO identity, or because the push
///     to ADO failed and we fell back to local staging).</item>
/// </list>
/// </remarks>
public abstract record NoteOutcome
{
    private NoteOutcome() { }

    /// <summary>Comment was successfully pushed to ADO.</summary>
    /// <param name="UpdatedItem">Latest work item (resynced from ADO when possible).</param>
    /// <param name="Warnings">Non-fatal warnings (e.g. cache-resync failure).</param>
    public sealed record Pushed(
        WorkItem UpdatedItem,
        IReadOnlyList<string> Warnings) : NoteOutcome;

    /// <summary>Comment was staged locally instead of being pushed to ADO.</summary>
    /// <param name="Item">The work item the comment was staged against.</param>
    /// <param name="WasOfflineFallback"><c>true</c> when staging happened because the ADO push failed; <c>false</c> for seeds (which always stage).</param>
    /// <param name="FailureReason">Underlying push-failure message when <paramref name="WasOfflineFallback"/> is <c>true</c>; otherwise <c>null</c>.</param>
    /// <param name="Warnings">Non-fatal warnings accumulated during execution.</param>
    public sealed record Staged(
        WorkItem Item,
        bool WasOfflineFallback,
        string? FailureReason,
        IReadOnlyList<string> Warnings) : NoteOutcome;
}
