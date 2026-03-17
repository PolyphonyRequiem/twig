using System.Globalization;
using Spectre.Console;

namespace Twig.Rendering;

/// <summary>
/// Converts hex color strings to Spectre.Console <see cref="Color"/> objects and markup color strings.
/// Accepts 6-digit RGB, 8-digit ARGB, and optional <c>#</c> prefix.
/// </summary>
internal static class HexToSpectreColor
{
    /// <summary>
    /// Converts a hex color string to a Spectre.Console <see cref="Color"/>.
    /// Returns null for invalid or null input.
    /// </summary>
    internal static Color? ToColor(string? hex)
    {
        var (r, g, b) = ParseRgb(hex);
        if (r is null)
            return null;

        return new Color(r.Value, g!.Value, b!.Value);
    }

    /// <summary>
    /// Converts a hex color string to a Spectre markup color string (e.g. <c>#FF7B00</c>).
    /// Returns null for invalid or null input.
    /// </summary>
    internal static string? ToMarkupColor(string? hex)
    {
        var (r, g, b) = ParseRgb(hex);
        if (r is null)
            return null;

        return $"#{r.Value:X2}{g!.Value:X2}{b!.Value:X2}";
    }

    private static (byte? R, byte? G, byte? B) ParseRgb(string? hex)
    {
        if (hex is not null && hex.Length > 0 && hex[0] == '#')
            hex = hex.Substring(1);

        // 8-char ARGB — strip the 2-char alpha prefix
        if (hex is not null && hex.Length == 8)
            hex = hex.Substring(2);

        if (hex is null || hex.Length != 6)
            return (null, null, null);

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b))
            return (null, null, null);

        return (r, g, b);
    }
}
