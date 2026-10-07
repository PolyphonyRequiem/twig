using Twig.Domain.Aggregates;
using Twig.Domain.Enums;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Result of pinning or unpinning an item on a Bench, produced by <c>PinWorkflow</c>.
/// </summary>
/// <remarks>
/// Both the CLI (<c>TrackingCommand</c>) and the agent surface (<c>TrackingTools</c>) render this
/// outcome; neither decides what pinning MEANS. The variants are deliberately few — a pin either
/// went onto the Bench or came off it, and "came off nothing" is reported rather than being an
/// error, because unpinning something that was never pinned is not a failure the person can act on.
/// </remarks>
public abstract record PinOutcome
{
    private PinOutcome() { }

    /// <summary>The selector is on the Bench.</summary>
    /// <param name="Bench">The Bench that was written to.</param>
    /// <param name="WorkItemId">The item that was pinned.</param>
    /// <param name="IncludesSubtree">
    /// True when a SUBTREE selector was added — one that matches the item's descendants as they
    /// are at every later look, including children created after the pin.
    /// </param>
    public sealed record Pinned(
        Bench Bench,
        int WorkItemId,
        bool IncludesSubtree) : PinOutcome;

    /// <summary>The requested explicit pin kind, or both kinds, is absent on the committed Bench.</summary>
    /// <param name="Bench">The Bench as read inside the committed pin transaction.</param>
    /// <param name="WorkItemId">The item that was unpinned.</param>
    /// <param name="WasPinned">True only when this transaction actually removed a requested explicit selector.</param>
    public sealed record Unpinned(
        Bench Bench,
        int WorkItemId,
        bool WasPinned) : PinOutcome
    {
        /// <summary>The requested kind; null means both explicit kinds were removed.</summary>
        public TrackingMode? Mode { get; init; }
    }
}
