namespace Twig.Mcp.Tools;

/// <summary>
/// Shared descriptions for parameters repeated across the MCP tool catalog.
/// Keep these concise because every occurrence is included in clients' initial tool metadata.
/// </summary>
internal static class McpToolDescriptions
{
    public const string WorkspaceOverride =
        "Workspace override (\"org/project\"). Omit for repo-local inference; " +
        "set only to disambiguate or retarget.";

    public const string BatchWorkspaceOverride =
        WorkspaceOverride + " Applied to steps without an explicit workspace.";
}
