using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Determines the ceiling backlog level for trimming parent chains
/// in the sprint hierarchy view. Pure static function — no instance state.
/// </summary>
public static class CeilingComputer
{
    /// <summary>
    /// Computes the ceiling type names — all type names belonging to the backlog level
    /// one above the highest level that any sprint item type belongs to.
    /// </summary>
    /// <param name="sprintItemTypeNames">Work item type names present in the sprint.</param>
    /// <param name="config">Process configuration with backlog level definitions.</param>
    /// <returns>
    /// All type names from the level above the highest matching level,
    /// or <c>null</c> if no parent context is needed (top-level items, no match, or null inputs).
    /// </returns>
    public static IReadOnlyList<string>? Compute(IReadOnlyList<string>? sprintItemTypeNames, ProcessConfigurationData? config)
    {
        if (config is null || sprintItemTypeNames is null || sprintItemTypeNames.Count == 0)
            return null;

        // Build ordered levels top-to-bottom: PortfolioBacklogs[0..N] + RequirementBacklog + TaskBacklog
        var levels = new List<BacklogLevelConfiguration>();
        if (config.PortfolioBacklogs is not null)
            levels.AddRange(config.PortfolioBacklogs);
        if (config.RequirementBacklog is not null)
            levels.Add(config.RequirementBacklog);
        if (config.TaskBacklog is not null)
            levels.Add(config.TaskBacklog);

        if (levels.Count == 0)
            return null;

        // Find the highest level index (lowest number = nearest to top) where any sprint type matches
        var highestMatchIndex = -1;
        for (var i = 0; i < levels.Count; i++)
        {
            foreach (var typeName in levels[i].WorkItemTypeNames)
            {
                if (sprintItemTypeNames.Any(s => string.Equals(s, typeName, StringComparison.OrdinalIgnoreCase)))
                {
                    highestMatchIndex = i;
                    break;
                }
            }

            if (highestMatchIndex >= 0)
                break;
        }

        // No match found, or match is at the top level — no parent context available
        if (highestMatchIndex <= 0)
            return null;

        // Return all type names from the level above
        var ceilingLevel = levels[highestMatchIndex - 1];
        return ceilingLevel.WorkItemTypeNames.Count > 0 ? ceilingLevel.WorkItemTypeNames : null;
    }
}
