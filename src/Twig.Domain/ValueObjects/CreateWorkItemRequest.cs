namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable DTO for the <c>CreateAsync</c> write path.
/// Carries only the data needed to create a work item in ADO,
/// decoupling callers from the full <see cref="Aggregates.WorkItem"/> aggregate.
/// </summary>
public sealed record CreateWorkItemRequest
{
    public required string TypeName { get; init; }
    public required string Title { get; init; }
    public string? AreaPath { get; init; }
    public string? IterationPath { get; init; }
    public int? ParentId { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
