using Twig.Infrastructure.Config;

namespace Twig.Mcp;

// FR-11: Extracted from top-level Program.cs so guard logic can be unit-tested.
internal static class WorkspaceGuard
{
    /// <summary>
    /// Ambient-mode guard for MCP multi-workspace: walks up from <paramref name="cwd"/>
    /// to find <c>.twig/</c>, then succeeds if any <c>.twig/{org}/{project}/config</c>
    /// exists OR the legacy <c>.twig/config</c> exists. Does not require the top-level
    /// <c>.twig/config</c> when per-workspace configs are present.
    /// </summary>
    internal static (bool IsValid, string? Error, string? TwigDir) CheckWorkspaceAmbient(string cwd)
    {
        var twigDir = WorkspaceDiscovery.FindTwigDir(cwd);
        if (twigDir is null)
            return (false, "No twig workspace found. Run 'twig init' in your project root.", null);

        // Legacy single-workspace: .twig/config
        if (File.Exists(Path.Combine(twigDir, "config")))
            return (true, null, twigDir);

        // Multi-workspace: .twig/{org}/{project}/config
        if (HasAnyWorkspaceConfig(twigDir))
            return (true, null, twigDir);

        return (false, "Twig workspace not initialized. Run 'twig init' first.", null);
    }

    private static bool HasAnyWorkspaceConfig(string twigDir)
    {
        try
        {
            foreach (var orgDir in Directory.GetDirectories(twigDir))
            {
                foreach (var projectDir in Directory.GetDirectories(orgDir))
                {
                    if (File.Exists(Path.Combine(projectDir, "config")))
                        return true;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        return false;
    }
}
