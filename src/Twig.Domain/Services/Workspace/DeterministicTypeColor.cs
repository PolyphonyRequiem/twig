namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Produces a deterministic ANSI color escape for a given type name
/// by hashing the string into one of six predefined colors.
/// </summary>
public static class DeterministicTypeColor
{
    private static readonly string[] AnsiColors =
    [
        "\x1b[35m", // Magenta
        "\x1b[36m", // Cyan
        "\x1b[34m", // Blue
        "\x1b[33m", // Yellow
        "\x1b[32m", // Green
        "\x1b[31m", // Red
    ];

    /// <summary>
    /// Returns a deterministic 3-bit ANSI color escape for the given <paramref name="typeName"/>.
    /// Uses a simple hash (<c>hash * 31 + c</c>) to assign a stable color per type name.
    /// </summary>
    public static string GetAnsiEscape(string typeName)
    {
        var hash = 0;
        foreach (var c in typeName)
            hash = hash * 31 + c;

        return AnsiColors[(hash & 0x7FFFFFFF) % AnsiColors.Length];
    }
}
