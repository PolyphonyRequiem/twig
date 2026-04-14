using Twig.Domain.Interfaces;

namespace Twig.Mcp.Services;

/// <summary>
/// Headless pending-change flusher for MCP.
/// Unlike the CLI's <c>PendingChangeFlusher</c>, this variant:
/// <list type="bullet">
///   <item>Has no <c>IConsoleInput</c> dependency — auto-accepts remote on conflict</item>
///   <item>Does not use <c>OutputFormatterFactory</c> — MCP tools handle their own output</item>
///   <item>Does not implement <c>IPendingChangeFlusher</c> (that interface has CLI-specific parameters)</item>
/// </list>
/// Placeholder — flush logic will be implemented in a subsequent task.
/// </summary>
public sealed class McpPendingChangeFlusher(
    IWorkItemRepository workItemRepo,
    IAdoWorkItemService adoService,
    IPendingChangeStore pendingChangeStore)
{
    private readonly IWorkItemRepository _workItemRepo = workItemRepo;
    private readonly IAdoWorkItemService _adoService = adoService;
    private readonly IPendingChangeStore _pendingChangeStore = pendingChangeStore;
}
