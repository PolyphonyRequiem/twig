namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Registry of known companion tool names. Single source of truth for
/// <see cref="SelfUpdater"/>, <c>SelfUpdateCommand</c>, and the first-run companion check.
/// </summary>
internal static class CompanionTools
{
    /// <summary>All companion binary base names (without platform extension).</summary>
    internal static readonly string[] All = ["twig-mcp", "twig-tui"];

    /// <summary>
    /// Returns the platform-specific executable name for a companion tool
    /// (e.g., <c>twig-mcp.exe</c> on Windows, <c>twig-mcp</c> on Unix).
    /// </summary>
    internal static string GetExeName(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;
}

/// <summary>
/// Result of a full self-update operation (main binary + companions).
/// In-process only — never serialized to JSON.
/// </summary>
public sealed record UpdateResult(
    string MainBinaryPath,
    IReadOnlyList<CompanionUpdateResult> Companions);

/// <summary>
/// Result of installing a single companion binary.
/// <paramref name="Found"/> indicates whether the binary was present in the archive.
/// </summary>
public sealed record CompanionUpdateResult(string Name, bool Found, string? InstalledPath);


