namespace Twig.Mcp;

// FR-11: Extracted from top-level Program.cs so guard logic can be unit-tested.
internal static class WorkspaceGuard
{
    internal static (bool IsValid, string? Error) CheckWorkspace(string cwd)
    {
        var configPath = Path.Combine(cwd, ".twig", "config");
        return File.Exists(configPath)
            ? (true, null)
            : (false, "Twig workspace not initialized. Run 'twig init' first.");
    }
}
