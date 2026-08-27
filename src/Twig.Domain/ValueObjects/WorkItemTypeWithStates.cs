namespace Twig.Domain.ValueObjects;

/// <summary>
/// A work item type with its ordered state sequence, used during init/refresh to populate
/// the <c>process_types</c> table.
/// </summary>
public sealed class WorkItemTypeWithStates
{
    public string Name { get; init; } = string.Empty;
    public string? Color { get; init; }
    public string? IconId { get; init; }
    public IReadOnlyList<WorkItemTypeState> States { get; init; } = Array.Empty<WorkItemTypeState>();

    /// <summary>
    /// Reference names of every work item type category this type belongs to (AB#656).
    /// A SET, because ADO's relation is many-to-many — see
    /// <see cref="WorkItemTypeCategories"/>.
    /// </summary>
    public IReadOnlyList<string> CategoryReferenceNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A single state within a work item type's workflow, including its category.
/// </summary>
public sealed class WorkItemTypeState
{
    public string Name { get; init; } = string.Empty;

    /// <summary>ADO state category: Proposed, InProgress, Resolved, Completed, Removed.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Hex color string from ADO (e.g. "007acc"), or null.</summary>
    public string? Color { get; init; }
}
