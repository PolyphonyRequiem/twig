namespace Twig.Infrastructure.Config;

/// <summary>
/// Locates the nearest manifest-owned workspace by walking up the
/// directory tree from a starting point — analogous to how git finds <c>.git/</c>.
/// <para>
/// Recognizes only a repo-root <c>twig.json</c> manifest as a workspace
/// marker. A repo with only <c>twig.json</c> committed (fresh clone, no local
/// <c>.twig/</c> yet) is a valid workspace; the <c>.twig/</c> directory is
/// created on demand by commands that need it.
/// </para>
/// </summary>
public static class WorkspaceDiscovery
{
    /// <summary>
    /// The AB#736 §4.3 system-local storage root. On Linux, honors
    /// <c>$XDG_STATE_HOME/twig</c> when the variable is set; otherwise falls
    /// back to the platform-appropriate <c>~/.twig</c>. Contains binaries,
    /// token cache, and the system registry — NOT a workspace.
    /// </summary>
    internal static string GlobalHomePath { get; } = ResolveSystemStateRoot();

    private static string ResolveSystemStateRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (!string.IsNullOrEmpty(xdgState))
                return Path.Combine(xdgState, "twig");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".twig");
    }

    /// <summary>
    /// AB#3296: the committed manifest filename at the repo root.
    /// </summary>
    public const string RepoManifestFileName = "twig.json";

    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> (or CWD if null)
    /// looking for a repo-root <c>twig.json</c> manifest. Returns the <c>.twig/</c> path
    /// (possibly synthesized as <c>&lt;repo-root&gt;/.twig</c> when only the
    /// manifest exists), or <c>null</c> if no workspace marker is found.
    /// Skips the global home at <c>~/.twig/</c> which holds machine state
    /// but is not a workspace.
    /// </summary>
    public static string? FindTwigDir(string? startDir = null)
    {
        var root = FindWorkspaceRoot(startDir);
        return root is null ? null : Path.Combine(root, ".twig");
    }

    /// <summary>
    /// Walks up from <paramref name="startDir"/> (or CWD) looking for the
    /// first ancestor with a <c>twig.json</c> manifest. Returns its directory path, or
    /// <c>null</c> if no marker is found.
    /// </summary>
    public static string? FindWorkspaceRoot(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();

        while (dir is not null)
        {
            if (IsWorkspaceDirectory(Path.Combine(dir, ".twig")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Determines whether a <c>.twig/</c> path belongs to a manifest-owned workspace.
    /// The global home at <c>~/.twig/</c> is always excluded regardless of contents.
    /// The <c>.twig/</c> directory itself does not need to exist when the committed
    /// manifest is present; commands create local workspace state on demand.
    /// </summary>
    public static bool IsWorkspaceDirectory(string twigDirPath)
    {
        // AB#2591: Never treat ~/.twig/ (global home) as a workspace.
        // Compare normalized full paths to handle casing/trailing separators.
        var normalizedCandidate = Path.GetFullPath(twigDirPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedHome = Path.GetFullPath(GlobalHomePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedHome, StringComparison.OrdinalIgnoreCase))
            return false;

        var repoRoot = Path.GetDirectoryName(normalizedCandidate);
        return repoRoot is not null && File.Exists(Path.Combine(repoRoot, RepoManifestFileName));
    }
}
