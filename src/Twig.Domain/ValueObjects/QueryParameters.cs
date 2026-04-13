namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable parameter bag that captures every filter the query command can accept.
/// Consumed by the WIQL query builder to produce a parameterised WHERE clause.
/// </summary>
public sealed record QueryParameters
{
    /// <summary>Free-text keyword for CONTAINS clause.</summary>
    public string? SearchText { get; init; }

    /// <summary>Work item type filter (exact match).</summary>
    public string? TypeFilter { get; init; }

    /// <summary>Work item state filter (exact match).</summary>
    public string? StateFilter { get; init; }

    /// <summary>Assignee display name filter.</summary>
    public string? AssignedToFilter { get; init; }

    /// <summary>Explicit area path from CLI flag (uses UNDER operator).</summary>
    public string? AreaPathFilter { get; init; }

    /// <summary>Explicit iteration path from CLI flag (uses UNDER operator).</summary>
    public string? IterationPathFilter { get; init; }

    /// <summary>Days since creation for @Today - N filter.</summary>
    public int? CreatedSinceDays { get; init; }

    /// <summary>Days since last change for @Today - N filter.</summary>
    public int? ChangedSinceDays { get; init; }

    /// <summary>Server-side result limit.</summary>
    public int Top { get; init; } = 25;

    /// <summary>
    /// Default area paths from config, carrying per-entry IncludeChildren flag.
    /// When set, these are combined with OR using the UNDER or = operator based on each entry's flag.
    /// </summary>
    public IReadOnlyList<(string Path, bool IncludeChildren)>? DefaultAreaPaths { get; init; }
}
