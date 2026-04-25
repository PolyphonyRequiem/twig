namespace Twig.Domain.ValueObjects;

/// <summary>
/// A work item explicitly excluded from a workspace view.
/// </summary>
/// <param name="WorkItemId">The ADO work item ID.</param>
/// <param name="Reason">Human-readable reason for exclusion.</param>
/// <param name="ExcludedAt">When the item was excluded.</param>
public sealed record ExcludedItem(int WorkItemId, string Reason, DateTimeOffset ExcludedAt);
