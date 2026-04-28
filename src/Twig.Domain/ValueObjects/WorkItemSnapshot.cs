namespace Twig.Domain.ValueObjects;

/// <summary>
/// Immutable record carrying raw work item data without domain behavior.
/// Uses primitive/string types for all fields — no value objects.
/// This is the boundary type that both ADO and SQLite mappers produce.
/// </summary>
public sealed record WorkItemSnapshot
{
    public int Id { get; init; }
    public int Revision { get; init; }
    public string TypeName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? AssignedTo { get; init; }
    public string? IterationPath { get; init; }
    public string? AreaPath { get; init; }
    public int? ParentId { get; init; }
    public bool IsSeed { get; init; }
    public DateTimeOffset? SeedCreatedAt { get; init; }
    public DateTimeOffset? LastSyncedAt { get; init; }
    public bool IsDirty { get; init; }
    public IReadOnlyDictionary<string, string?> Fields { get; init; }
        = new Dictionary<string, string?>();
}
