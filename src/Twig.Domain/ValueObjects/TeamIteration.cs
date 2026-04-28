namespace Twig.Domain.ValueObjects;

/// <summary>
/// Represents a single team iteration (sprint) from the ADO team settings.
/// Contains the iteration path and optional start/end dates.
/// </summary>
public sealed record TeamIteration(string Path, DateTimeOffset? StartDate, DateTimeOffset? EndDate);
