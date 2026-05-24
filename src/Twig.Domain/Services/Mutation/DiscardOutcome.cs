using System.Collections.Generic;
using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Discriminated-union result produced by <c>DiscardWorkflow.ExecuteAsync</c>.
/// </summary>
/// <remarks>
/// Adapters (<c>DiscardCommand</c>'s single-item path, MCP <c>twig_discard</c>)
/// inspect the variant to render the correct success / hint envelope.
/// The <c>--all</c> CLI flow does its own batch-clear and does not route through
/// this workflow.
/// </remarks>
public abstract record DiscardOutcome
{
    private DiscardOutcome() { }

    /// <summary>Work item had no pending changes and no dirty flag — true no-op.</summary>
    public sealed record NoChanges(WorkItem Item) : DiscardOutcome;

    /// <summary>Work item had a stale dirty flag (no real pending changes) which was cleared.</summary>
    public sealed record PhantomDirtyCleared(WorkItem Item, IReadOnlyList<string> Warnings) : DiscardOutcome;

    /// <summary>Pending changes were cleared.</summary>
    public sealed record Discarded(
        WorkItem Item,
        int NotesCount,
        int FieldEditsCount,
        IReadOnlyList<string> Warnings) : DiscardOutcome;
}
