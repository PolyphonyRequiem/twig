namespace Twig.Infrastructure.Config;

/// <summary>
/// Locates the nearest <c>.twig/</c> workspace directory by walking up the
/// directory tree from a starting point — analogous to how git finds <c>.git/</c>.
/// </summary>
public static class WorkspaceDiscovery
{
    /// <summary>
    /// The well-known global home directory: <c>~/.twig/</c>.
    /// Contains binaries, token cache, and profiles — NOT a workspace.
    /// </summary>
    internal static string GlobalHomePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".twig");

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> (or CWD if null)
    /// looking for a <c>.twig/</c> subdirectory that is a valid workspace.
    /// Skips the global home at <c>~/.twig/</c> which holds binaries and profiles
    /// but is not a workspace. Returns the full path to the first workspace
    /// <c>.twig/</c> found, or <c>null</c> if none exists up to the filesystem root.
    /// </summary>
    public static string? FindTwigDir(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".twig");
            if (Directory.Exists(candidate) && IsWorkspaceDirectory(candidate))
                return candidate;

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Determines whether a <c>.twig/</c> directory is an actual workspace
    /// (as opposed to the global home). A workspace contains either:
    /// <list type="bullet">
    /// <item>A <c>config</c> file (legacy or current workspace config), OR</item>
    /// <item>A nested <c>{org}/{project}/twig.db</c> database file, OR</item>
    /// <item>A nested <c>{org}/{project}/config</c> file (multi-workspace layout).</item>
    /// </list>
    /// The global home at <c>~/.twig/</c> is always excluded regardless of contents.
    /// </summary>
    public static bool IsWorkspaceDirectory(string twigDirPath)
    {
        // AB#2591: Never treat ~/.twig/ (global home) as a workspace.
        // Compare normalized full paths to handle casing/trailing separators.
        var normalizedCandidate = Path.GetFullPath(twigDirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedHome = Path.GetFullPath(GlobalHomePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedHome, StringComparison.OrdinalIgnoreCase))
            return false;

        // A valid workspace has a config file at its root
        if (File.Exists(Path.Combine(twigDirPath, "config")))
            return true;

        // Or has at least one nested context: {org}/{project}/twig.db or {org}/{project}/config
        if (HasNestedWorkspaceContext(twigDirPath))
            return true;

        return false;
    }

    private static bool HasNestedWorkspaceContext(string twigDirPath)
    {
        try
        {
            foreach (var orgDir in Directory.EnumerateDirectories(twigDirPath))
            {
                foreach (var projectDir in Directory.EnumerateDirectories(orgDir))
                {
                    if (File.Exists(Path.Combine(projectDir, "twig.db"))
                        || File.Exists(Path.Combine(projectDir, "config")))
                        return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Filesystem access issues — treat as not a workspace
        }

        return false;
    }
}
