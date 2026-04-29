namespace Twig.Mcp.Services;

/// <summary>
/// Contextual metadata included in every MCP envelope response.
/// Populated automatically by <see cref="EnvelopeBuilder"/> from the current workspace state.
/// </summary>
/// <param name="ActiveItemId">The currently active work item ID, or <c>null</c> if no item is set.</param>
/// <param name="Workspace">The workspace key (format: <c>"org/project"</c>), or <c>""</c> if unknown.</param>
/// <param name="CacheAge">
/// ISO 8601 duration since the active item was last synced (e.g. <c>"PT2M30S"</c>),
/// or <c>""</c> when no sync timestamp is available.
/// </param>
public sealed record McpContext(int? ActiveItemId, string Workspace, string CacheAge);
