namespace Twig.Infrastructure.GitHub;

/// <summary>
/// Registry of separately built companion tools and same-image native launch names.
/// Shared by self-update and the first-run companion check.
/// </summary>
internal static class CompanionTools
{
    /// <summary>Separately built companions. Only macOS retains the legacy TUI.</summary>
    internal static readonly string[] All = OperatingSystem.IsMacOS() ? ["twig-tui"] : [];

    /// <summary>Windows/Linux standalone entry names copied from the main native image.</summary>
    internal static readonly string[] NativeLaunchNames = ["twig-tui", "twig-bench-tui"];

    /// <summary>Names synchronized with an explicit upgrade; native aliases are not first-run companions.</summary>
    internal static readonly string[] UpdateTargets = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? NativeLaunchNames : All;

    internal static bool IsNativeLaunchExecutable(string name)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return false;
        var basename = name.AsSpan();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (OperatingSystem.IsWindows())
        {
            if (!basename.EndsWith(".exe", comparison)) return false;
            basename = basename[..^4];
        }
        foreach (var launchName in NativeLaunchNames)
            if (basename.Equals(launchName, comparison)) return true;
        return false;
    }

    /// <summary>
    /// Returns the platform-specific executable name for a companion tool
    /// (e.g., <c>twig-tui.exe</c> on Windows, <c>twig-tui</c> on Unix).
    /// </summary>
    internal static string GetExeName(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;
}

/// <summary>
/// Abstraction for companion installation, enabling NSubstitute mocking
/// of the sealed <see cref="SelfUpdater"/> in unit tests (e.g., <c>CompanionFirstRunCheck</c>).
/// </summary>
internal interface ICompanionInstaller
{
    Task<IReadOnlyList<CompanionUpdateResult>> InstallCompanionsOnlyAsync(
        string archiveUrl,
        string archiveName,
        IReadOnlyList<string> companionExeNames,
        string installDir,
        CancellationToken ct = default,
        bool force = false);
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
