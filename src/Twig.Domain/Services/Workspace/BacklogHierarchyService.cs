using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Infers parent→child work item type relationships from the ADO backlog hierarchy.
/// Pure function — no state, no side effects.
/// </summary>
public static class BacklogHierarchyService
{
    /// <summary>
    /// Infers parent→children type name mappings from a backlog hierarchy.
    /// Algorithm: levels = portfolioBacklogs[0..N] + requirementBacklog + taskBacklog (top to bottom).
    /// Each level's types become children of the type(s) in the level above.
    /// Bug types are excluded (their parent-child behavior is team-setting dependent).
    /// Algorithm documented in twig-dynamic-process.plan.md §7 "Backlog Hierarchy → Parent-Child Inference Algorithm".
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> InferParentChildMap(ProcessConfigurationData? config)
    {
        if (config is null)
            return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var levels = BuildLevels(config);
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < levels.Count - 1; i++)
        {
            var parentLevel = levels[i];
            var childLevel = levels[i + 1];
            var childTypeNames = childLevel.WorkItemTypeNames.ToList();

            foreach (var parentTypeName in parentLevel.WorkItemTypeNames)
            {
                result[parentTypeName] = childTypeNames;
            }
        }

        return result;
    }

    /// <summary>
    /// Maps each work item type name to its backlog level (0 = top portfolio, 1 = next portfolio/requirement, 2 = task, etc.).
    /// Uses the same ordered backlog levels as <see cref="InferParentChildMap"/>.
    /// Returns an empty dictionary when <paramref name="config"/> is null.
    /// </summary>
    public static IReadOnlyDictionary<string, int> GetTypeLevelMap(ProcessConfigurationData? config)
    {
        if (config is null)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var levels = BuildLevels(config);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < levels.Count; i++)
        {
            foreach (var typeName in levels[i].WorkItemTypeNames)
            {
                result[typeName] = i;
            }
        }

        return result;
    }

    private static List<BacklogLevelConfiguration> BuildLevels(ProcessConfigurationData config)
    {
        var levels = new List<BacklogLevelConfiguration>();
        if (config.PortfolioBacklogs is not null)
            levels.AddRange(config.PortfolioBacklogs);
        if (config.RequirementBacklog is not null)
            levels.Add(config.RequirementBacklog);
        if (config.TaskBacklog is not null)
            levels.Add(config.TaskBacklog);
        return levels;
    }
}
