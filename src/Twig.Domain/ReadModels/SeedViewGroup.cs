using Twig.Domain.Aggregates;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Read model grouping a parent work item with its seed children.
/// </summary>
public sealed record SeedViewGroup(
    WorkItem? Parent,
    IReadOnlyList<WorkItem> Seeds);
