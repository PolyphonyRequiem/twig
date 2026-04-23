namespace Twig.Domain.ValueObjects;

/// <summary>
/// A work item explicitly tracked in a workspace mode.
/// The <paramref name="TrackingMode"/> indicates whether to show the item alone ("single")
/// or include its subtree ("tree").
/// </summary>
/// <param name="Id">The ADO work item ID.</param>
/// <param name="TrackingMode">Tracking mode: "single" or "tree".</param>
/// <param name="CreatedAt">When the item was added to tracking.</param>
public sealed record TrackedItem(int Id, string TrackingMode, DateTimeOffset CreatedAt);
