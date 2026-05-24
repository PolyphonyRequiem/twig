using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated union describing the outcome of a single-field update workflow
/// after a published work item has been fetched and conflict-resolved by the caller.
/// </summary>
/// <remarks>
/// Adapters (<c>UpdateCommand</c>, <c>MutationTools.Update</c>) <c>switch</c> on
/// the concrete type and render the appropriate UI / envelope. The default arm
/// must throw <see cref="System.Diagnostics.UnreachableException"/>.
/// </remarks>
public abstract record FieldUpdateOutcome
{
    private FieldUpdateOutcome() { }

    /// <summary>
    /// The PATCH succeeded after at most one retry of the conflict-resolution flow.
    /// <see cref="UpdatedItem"/> is the post-mutation work item (best-effort resynced).
    /// <see cref="Warnings"/> collects best-effort side-effect failures
    /// (auto-push notes, cache resync, prompt-state write) that should be surfaced
    /// without changing the exit code.
    /// </summary>
    public sealed record Succeeded(
        WorkItem UpdatedItem,
        IReadOnlyList<string> Warnings) : FieldUpdateOutcome;

    /// <summary>
    /// The PATCH was rejected with a concurrency conflict even after
    /// <see cref="Twig.Infrastructure.Ado.ConflictRetryHelper.PatchWithRetryAsync"/>
    /// re-fetched and retried. The caller should advise <c>twig sync</c> + retry.
    /// </summary>
    public sealed record ConflictAfterRetry() : FieldUpdateOutcome;
}
