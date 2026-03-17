using System.Text.RegularExpressions;

namespace Twig.Domain.ValueObjects;

/// <summary>
/// Deterministic slugification for branch name segments.
/// Rules: lowercase, spaces/underscores → hyphens, strip non-alphanumeric (except hyphens),
/// collapse consecutive hyphens, truncate to maxLength, trim trailing hyphens.
/// </summary>
public static partial class SlugHelper
{
    /// <summary>
    /// Converts a free-text string into a URL/branch-safe slug.
    /// </summary>
    public static string Slugify(string input, int maxLength = 50)
    {
        if (string.IsNullOrWhiteSpace(input) || maxLength <= 0)
            return string.Empty;

        var slug = input.ToLowerInvariant();

        // Spaces and underscores → hyphens
        slug = SpacesAndUnderscores().Replace(slug, "-");

        // Strip non-alphanumeric (except hyphens)
        slug = NonAlphanumeric().Replace(slug, "");

        // Collapse consecutive hyphens
        slug = ConsecutiveHyphens().Replace(slug, "-");

        // Trim leading/trailing hyphens before truncation
        slug = slug.Trim('-');

        // Truncate to maxLength
        if (slug.Length > maxLength)
            slug = slug[..maxLength];

        // Trim trailing hyphens after truncation
        slug = slug.TrimEnd('-');

        return slug;
    }

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex SpacesAndUnderscores();

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex ConsecutiveHyphens();
}
