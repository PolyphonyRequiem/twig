namespace Twig.Infrastructure.Config;

/// <summary>
/// Locates the nearest <c>.twig/</c> workspace directory by walking up the
/// directory tree from a starting point — analogous to how git finds <c>.git/</c>.
/// </summary>
public static class WorkspaceDiscovery
{
    /// <summary>
    /// Walks up the directory tree from <paramref name="startDir"/> (or CWD if null)
    /// looking for a <c>.twig/</c> subdirectory. Returns the full path to the first
    /// <c>.twig/</c> found, or <c>null</c> if none exists up to the filesystem root.
    /// </summary>
    public static string? FindTwigDir(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();

        while (dir is not null)
        {
            var candidate = Path.Combine(dir, ".twig");
            if (Directory.Exists(candidate))
                return candidate;

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }
}
