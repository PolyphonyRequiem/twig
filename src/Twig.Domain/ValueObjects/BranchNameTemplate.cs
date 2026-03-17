using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Generates branch names from configurable templates and extracts work item IDs from branch names.
/// Tokens: <c>{id}</c>, <c>{type}</c> (slugified), <c>{title}</c> (slugified via <see cref="SlugHelper"/>).
/// </summary>
public static class BranchNameTemplate
{
    /// <summary>
    /// Default template used when no custom template is configured.
    /// </summary>
    public const string DefaultTemplate = "feature/{id}-{title}";

    /// <summary>
    /// Default regex pattern for extracting a work item ID from a branch name.
    /// Matches 3+ digit sequences after a separator (<c>/</c>, <c>-</c>) or at start/end.
    /// </summary>
    public const string DefaultPattern = @"(?:^|/)(?<id>\d{3,})(?:-|/|$)";

    // Cache compiled regexes keyed by pattern string to avoid recompiling on every invocation.
    private static readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    /// <summary>
    /// Generates a branch name by replacing tokens in the template.
    /// </summary>
    public static string Generate(string template, int id, string type, string title)
    {
        var result = template
            .Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{type}", SlugHelper.Slugify(type), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", SlugHelper.Slugify(title), StringComparison.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>
    /// Extracts a work item ID from a branch name using the given regex pattern.
    /// The pattern must contain a named capture group <c>id</c>.
    /// Returns <c>null</c> if no match is found.
    /// </summary>
    public static int? ExtractWorkItemId(string branchName, string pattern)
    {
        if (string.IsNullOrEmpty(branchName) || string.IsNullOrEmpty(pattern))
            return null;

        try
        {
            var regex = _regexCache.GetOrAdd(pattern,
                static p => new Regex(p, RegexOptions.Compiled, TimeSpan.FromSeconds(1)));

            var match = regex.Match(branchName);
            if (match.Success && match.Groups["id"].Success &&
                int.TryParse(match.Groups["id"].Value, out var id))
            {
                return id;
            }

            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
