using System.Collections.Generic;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated-union result produced by <c>PatchWorkflow.ExecuteAsync</c>.
/// </summary>
/// <remarks>
/// Adapters (<c>PatchCommand</c>, <c>MutationTools.Patch</c>) inspect the
/// variant to render the correct envelope. Best-effort side-effect failures
/// (auto-push notes, cache resync, prompt-state write) accumulate into
/// <see cref="Patched.Warnings"/> / <see cref="SeedPatched.Warnings"/>
/// rather than failing the workflow.
/// </remarks>
public abstract record PatchOutcome
{
    private PatchOutcome() { }

    /// <summary>
    /// All field changes were applied locally against a seed work item.
    /// <see cref="Item"/> reflects the post-mutation seed; cache + prompt-state
    /// were refreshed best-effort.
    /// </summary>
    public sealed record SeedPatched(
        WorkItem Item,
        IReadOnlyList<FieldChange> Changes,
        IReadOnlyList<string> Warnings) : PatchOutcome;

    /// <summary>
    /// One of the seed field updates was rejected by
    /// <see cref="SeedMutationProvider"/>; no further changes were applied.
    /// </summary>
    public sealed record SeedFieldRejected(string FieldName, string Reason) : PatchOutcome;

    /// <summary>
    /// The PATCH succeeded after at most one retry.
    /// <see cref="UpdatedItem"/> is the post-mutation work item (best-effort resynced).
    /// </summary>
    public sealed record Patched(
        WorkItem UpdatedItem,
        IReadOnlyList<FieldChange> Changes,
        IReadOnlyList<string> Warnings) : PatchOutcome;

    /// <summary>
    /// The PATCH was rejected with a concurrency conflict even after retry.
    /// The caller should advise <c>twig sync</c> + retry.
    /// </summary>
    public sealed record ConflictAfterRetry : PatchOutcome;

    /// <summary>ADO call failed (network / auth / transient) outside of the conflict path.</summary>
    public sealed record AdoUnreachable(string Reason) : PatchOutcome;
}
