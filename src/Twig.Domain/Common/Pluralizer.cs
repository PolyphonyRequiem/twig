namespace Twig.Domain.Common;

/// <summary>
/// Simple English pluralization helper for work item type names.
/// Used by virtual group label construction (e.g., "Feature" → "Features", "Story" → "Stories").
/// </summary>
public static class Pluralizer
{
    /// <summary>
    /// Pluralizes a work item type name using basic English rules:
    /// names ending in consonant + "y" → replace "y" with "ies" (e.g., "Story" → "Stories");
    /// names ending in "s", "sh", "ch", "x", "z" → append "es";
    /// all others → append "s".
    /// </summary>
    public static string? Pluralize(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return typeName;

        if (typeName.Length >= 2
            && typeName[^1] is 'y' or 'Y'
            && !IsVowel(typeName[^2]))
        {
            return typeName[..^1] + (char.IsUpper(typeName[^1]) ? "IES" : "ies");
        }

        if (typeName.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith("sh", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith("ch", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith("x", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith("z", StringComparison.OrdinalIgnoreCase))
        {
            return typeName + "es";
        }

        return typeName + "s";
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u'
                                         or 'A' or 'E' or 'I' or 'O' or 'U';
}
