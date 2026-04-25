namespace Twig.Domain.ValueObjects;

/// <summary>
/// Domain-level representation of an ADO process configuration (backlog hierarchy).
/// Mapped from the infrastructure-level <c>AdoProcessConfigurationResponse</c> inside
/// <see cref="Twig.Infrastructure.Ado.AdoIterationService"/>. This DTO lives in the Domain layer so
/// <see cref="Twig.Domain.Interfaces.IIterationService"/> can reference it without crossing the
/// Domain→Infrastructure dependency boundary.
/// </summary>
public sealed class ProcessConfigurationData
{
    public BacklogLevelConfiguration? TaskBacklog { get; init; }
    public BacklogLevelConfiguration? RequirementBacklog { get; init; }
    public IReadOnlyList<BacklogLevelConfiguration> PortfolioBacklogs { get; init; }
        = Array.Empty<BacklogLevelConfiguration>();
    public BacklogLevelConfiguration? BugWorkItems { get; init; }
}

/// <summary>
/// A single backlog level containing a name and the work item type names that belong to that level.
/// </summary>
public sealed class BacklogLevelConfiguration
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> WorkItemTypeNames { get; init; } = Array.Empty<string>();
}
