using Twig.Infrastructure.Config;

namespace Twig.Mcp;

// FR-11: Extracted from top-level Program.cs so guard logic can be unit-tested.
internal static class WorkspaceGuard
{
    /// <summary>
    /// Ambient-mode guard for MCP multi-workspace. Uses the shared workspace
    /// discovery predicate so MCP agrees with CLI and TUI bootstrap.
    /// </summary>
    internal static (bool IsValid, string? Error, string? TwigDir) CheckWorkspaceAmbient(string cwd)
    {
        var workspaceRoot = WorkspaceDiscovery.FindWorkspaceRoot(cwd);
        if (workspaceRoot is null)
            return (false, "No twig workspace found. Run 'twig init' in your project root.", null);

        var twigDir = Path.Combine(workspaceRoot, ".twig");
        return (true, null, twigDir);
    }
}
