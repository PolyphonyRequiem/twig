using Twig.Domain.Enums;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// A work item explicitly tracked in a workspace mode.
/// </summary>
/// <param name="WorkItemId">The ADO work item ID.</param>
/// <param name="Mode">Whether to track the single item or its subtree.</param>
/// <param name="TrackedAt">When the item was added to tracking.</param>
public sealed record TrackedItem(int WorkItemId, TrackingMode Mode, DateTimeOffset TrackedAt);
