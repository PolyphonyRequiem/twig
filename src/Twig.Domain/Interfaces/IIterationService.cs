using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Service for detecting the current iteration and process template from Azure DevOps.
/// Implemented in Infrastructure (ADO API).
/// </summary>
public interface IIterationService
{
    Task<IterationPath> GetCurrentIterationAsync(CancellationToken ct = default);
    Task<string?> DetectTemplateNameAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkItemTypeAppearance>> GetWorkItemTypeAppearancesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string Path, bool IncludeChildren)>> GetTeamAreaPathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the authenticated user's display name from the ADO connection data endpoint.
    /// Returns null if detection fails.
    /// </summary>
    Task<string?> GetAuthenticatedUserDisplayNameAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all work item types with their ordered state sequences.
    /// Reuses the existing GET /_apis/wit/workitemtypes endpoint.
    /// Disabled types are excluded; null-color types are retained (custom types may lack colors).
    /// States are sorted by category rank: Proposed→InProgress→Resolved→Completed→Removed.
    /// </summary>
    Task<IReadOnlyList<WorkItemTypeWithStates>> GetWorkItemTypesWithStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the project-level process configuration (backlog hierarchy) for inferring
    /// parent-child type relationships. Calls GET /{project}/_apis/work/processconfiguration.
    /// Returns a domain DTO — not the internal infrastructure ADO response type.
    /// </summary>
    Task<ProcessConfigurationData> GetProcessConfigurationAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all field definitions for the project from the ADO fields API.
    /// Calls GET /{project}/_apis/wit/fields.
    /// </summary>
    Task<IReadOnlyList<FieldDefinition>> GetFieldDefinitionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all team iterations (sprints) from the ADO team settings.
    /// Calls GET /{project}/{team}/_apis/work/teamsettings/iterations without a timeframe filter.
    /// Results are returned in the order provided by ADO (chronological).
    /// </summary>
    Task<IReadOnlyList<TeamIteration>> GetTeamIterationsAsync(CancellationToken ct = default);
}
