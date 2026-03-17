namespace Twig.Domain.Services;

/// <summary>
/// Resolves a work-item type name to its hex color string by searching
/// user-override colors first, then ADO appearance colors.
/// Both dictionaries are normalized to case-insensitive lookup internally.
/// </summary>
public static class TypeColorResolver
{
    /// <summary>
    /// Resolves a hex color for the given <paramref name="typeName"/>.
    /// Priority: <paramref name="typeColors"/> (user overrides) → <paramref name="appearanceColors"/> (ADO data) → <c>null</c>.
    /// Performs case-insensitive matching on type names by internally
    /// normalizing each non-null dictionary to <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <remarks>
    /// The comparer optimization (<c>dictionary.Comparer == StringComparer.OrdinalIgnoreCase</c>)
    /// uses reference equality against the well-known singleton. Dictionaries constructed with
    /// <c>StringComparer.FromComparison(StringComparison.OrdinalIgnoreCase)</c> may return a
    /// different instance, causing an unnecessary (but functionally correct) copy.
    /// </remarks>
    public static string? ResolveHex(
        string typeName,
        Dictionary<string, string>? typeColors,
        Dictionary<string, string>? appearanceColors)
    {
        if (typeColors is not null)
        {
            var lookup = typeColors.Comparer == StringComparer.OrdinalIgnoreCase
                ? typeColors
                : new Dictionary<string, string>(typeColors, StringComparer.OrdinalIgnoreCase);
            if (lookup.TryGetValue(typeName, out var hex))
                return hex;
        }

        if (appearanceColors is not null)
        {
            var lookup = appearanceColors.Comparer == StringComparer.OrdinalIgnoreCase
                ? appearanceColors
                : new Dictionary<string, string>(appearanceColors, StringComparer.OrdinalIgnoreCase);
            if (lookup.TryGetValue(typeName, out var hex))
                return hex;
        }

        return null;
    }
}
