using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Navigation;

/// <summary>
/// Formats commit messages from work item context using configurable templates.
/// Template tokens: <c>{type}</c>, <c>{id}</c>, <c>{message}</c>, <c>{title}</c>.
/// The <c>{type}</c> token maps ADO work item types to conventional commit prefixes.
/// </summary>
public static class CommitMessageService
{
    /// <summary>
    /// Default mapping from ADO work item type names to conventional commit prefixes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultTypeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User Story"] = "feat",
            ["Product Backlog Item"] = "feat",
            ["Requirement"] = "feat",
            ["Feature"] = "feat",
            ["Bug"] = "fix",
            ["Task"] = "chore",
            ["Epic"] = "epic",
            ["Issue"] = "fix",
            ["Impediment"] = "chore",
            ["Test Case"] = "test",
        };

    /// <summary>
    /// Formats a commit message by substituting template tokens with work item and user-supplied values.
    /// </summary>
    /// <param name="workItem">The active work item providing context.</param>
    /// <param name="userMessage">The user-supplied commit message text.</param>
    /// <param name="template">
    /// The commit message template. Tokens: <c>{type}</c>, <c>{id}</c>, <c>{message}</c>, <c>{title}</c>.
    /// </param>
    /// <param name="typeMap">Optional custom type-to-prefix map. Falls back to <see cref="DefaultTypeMap"/>.</param>
    /// <returns>The formatted commit message string.</returns>
    public static string Format(
        WorkItem workItem,
        string userMessage,
        string template,
        IReadOnlyDictionary<string, string>? typeMap = null)
    {
        var commitType = ResolveType(workItem.Type.Value, typeMap);

        var result = template
            .Replace("{type}", commitType, StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", workItem.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{message}", userMessage, StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", workItem.Title, StringComparison.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>
    /// Resolves a work item type name to its conventional commit prefix.
    /// Custom type maps are searched case-insensitively.
    /// Falls back to the lowercased raw type name for unmapped types.
    /// </summary>
    public static string ResolveType(string workItemType, IReadOnlyDictionary<string, string>? typeMap)
    {
        if (typeMap is not null)
        {
            if (typeMap.TryGetValue(workItemType, out var mapped))
                return mapped;

            foreach (var kvp in typeMap)
            {
                if (string.Equals(kvp.Key, workItemType, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }

        if (DefaultTypeMap.TryGetValue(workItemType, out var defaultMapped))
            return defaultMapped;

        return workItemType.ToLowerInvariant();
    }
}
