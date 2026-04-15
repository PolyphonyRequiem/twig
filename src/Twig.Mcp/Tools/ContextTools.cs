using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for context management: twig.set, twig.status.
/// </summary>
[McpServerToolType]
public sealed class ContextTools(
    IWorkItemRepository workItemRepo,
    IContextStore contextStore,
    ActiveItemResolver activeItemResolver,
    StatusOrchestrator statusOrchestrator,
    IPromptStateWriter promptStateWriter,
    ContextChangeService contextChangeService)
{
    [McpServerTool(Name = "twig.set"), Description("Set the active work item by ID or title pattern")]
    public async Task<CallToolResult> Set(
        [Description("Work item ID (numeric) or title pattern (text)")] string idOrPattern,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrPattern))
            return McpResultBuilder.ToError("Usage: twig.set requires an ID or title pattern.");

        Domain.Aggregates.WorkItem item;

        if (int.TryParse(idOrPattern, out var id))
        {
            var result = await activeItemResolver.ResolveByIdAsync(id, ct);

            if (result is ActiveItemResult.Unreachable u)
                return McpResultBuilder.ToError($"Work item #{u.Id} unreachable: {u.Reason}");

            item = result is ActiveItemResult.Found f
                ? f.WorkItem
                : ((ActiveItemResult.FetchedFromAdo)result).WorkItem;
        }
        else
        {
            var matches = await workItemRepo.FindByPatternAsync(idOrPattern, ct);

            if (matches.Count == 0)
                return McpResultBuilder.ToError($"No cached items match '{idOrPattern}'.");

            if (matches.Count > 1)
            {
                var lines = matches.Select(m => $"  #{m.Id}: {m.Title} [{m.State}]");
                return McpResultBuilder.ToError(
                    $"Multiple matches — specify by ID:\n{string.Join("\n", lines)}");
            }

            item = matches[0];
        }

        await contextStore.SetActiveWorkItemIdAsync(item.Id, ct);

        // Extend working set around the target item (parent chain, 2 levels of children, links).
        // Best-effort — extension failures must never fail the tool call.
        try
        {
            await contextChangeService.ExtendWorkingSetAsync(item.Id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        await promptStateWriter.WritePromptStateAsync();

        // Compute working set summary for the response (post-extension snapshot)
        var parentChainCount = 0;
        if (item.ParentId.HasValue)
        {
            var chain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            parentChainCount = chain.Count;
        }
        var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
        return McpResultBuilder.FormatWorkItemWithWorkingSet(item, parentChainCount, children.Count);
    }

    [McpServerTool(Name = "twig.status"), Description("Show the active work item status")]
    public async Task<CallToolResult> Status(CancellationToken ct = default)
    {
        var snapshot = await statusOrchestrator.GetSnapshotAsync(ct);

        if (!snapshot.HasContext)
            return McpResultBuilder.ToError("No active work item. Use twig.set to set context.");

        return McpResultBuilder.FormatStatus(snapshot);
    }
}
