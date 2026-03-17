using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// Generates branch names from work items using configurable templates and type mapping.
/// Default type map: User Story→feature, Bug→bug, Task→task, Epic→epic, Feature→feature.
/// Falls back to slugified work item type name for unmapped types.
/// </summary>
public static class BranchNamingService
{
    /// <summary>
    /// Default type map used when no custom map is configured.
    /// Maps ADO work item type names to short branch-prefix tokens.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultTypeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User Story"] = "feature",
            ["Product Backlog Item"] = "feature",
            ["Requirement"] = "feature",
            ["Bug"] = "bug",
            ["Task"] = "task",
            ["Epic"] = "epic",
            ["Feature"] = "feature",
            ["Issue"] = "issue",
            ["Impediment"] = "impediment",
            ["Test Case"] = "test",
        };

    /// <summary>
    /// Generates a branch name for the given work item using the specified template.
    /// Tokens: <c>{id}</c>, <c>{type}</c>, <c>{title}</c>.
    /// The <c>{type}</c> token is resolved via <paramref name="typeMap"/> (or <see cref="DefaultTypeMap"/>),
    /// then slugified. The <c>{title}</c> token is slugified via <see cref="SlugHelper"/>.
    /// </summary>
    public static string Generate(WorkItem workItem, string template, IReadOnlyDictionary<string, string>? typeMap = null)
    {
        var resolvedType = ResolveType(workItem.Type.Value, typeMap);
        return BranchNameTemplate.Generate(template, workItem.Id, resolvedType, workItem.Title);
    }

    /// <summary>
    /// Resolves a work item type name to its branch token using the given map (or defaults).
    /// Falls back to the raw type name (which will be slugified by the template engine).
    /// </summary>
    internal static string ResolveType(string workItemType, IReadOnlyDictionary<string, string>? typeMap)
    {
        if (typeMap is not null && typeMap.TryGetValue(workItemType, out var mapped))
            return mapped;

        if (DefaultTypeMap.TryGetValue(workItemType, out var defaultMapped))
            return defaultMapped;

        return workItemType;
    }
}
