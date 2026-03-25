namespace Twig.Domain.ValueObjects;

public sealed record NavigationHistoryEntry(int Id, int WorkItemId, DateTimeOffset VisitedAt);
