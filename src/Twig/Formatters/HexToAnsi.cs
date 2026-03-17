using System.Globalization;

namespace Twig.Formatters;

/// <summary>
/// Converts hex color strings to 24-bit true color ANSI escape sequences.
/// </summary>
internal static class HexToAnsi
{
    /// <summary>
    /// Converts a hex color string to a 24-bit true color ANSI foreground escape sequence.
    /// Accepts 6-digit RGB (<c>FF7B00</c>), 8-digit ARGB (<c>FFFF7B00</c>), and optional
    /// <c>#</c> prefix. The alpha channel is ignored. Returns null for invalid input.
    /// </summary>
    internal static string? ToForeground(string? hex)
    {
        if (hex is not null && hex.Length > 0 && hex[0] == '#')
            hex = hex.Substring(1);

        // 8-char ARGB (e.g. "FFCC293D") — strip the 2-char alpha prefix
        if (hex is not null && hex.Length == 8)
            hex = hex.Substring(2);

        if (hex is null || hex.Length != 6)
            return null;

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b))
            return null;

        return $"\x1b[38;2;{r};{g};{b}m";
    }
}
