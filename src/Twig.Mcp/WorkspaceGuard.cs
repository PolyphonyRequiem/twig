using Twig.Infrastructure.Config;

namespace Twig.Mcp;

// FR-11: Extracted from top-level Program.cs so guard logic can be unit-tested.
internal static class WorkspaceGuard
{
    internal static (bool IsValid, string? Error, string? TwigDir) CheckWorkspace(string cwd)
    {
        var twigDir = WorkspaceDiscovery.FindTwigDir(cwd);
        if (twigDir is null)
            return (false, "No twig workspace found. Run 'twig init' in your project root.", null);
        var configPath = Path.Combine(twigDir, "config");
        return File.Exists(configPath)
            ? (true, null, twigDir)
            : (false, "Twig workspace not initialized. Run 'twig init' first.", null);
    }
}
