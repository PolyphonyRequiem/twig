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
    SyncCoordinator syncCoordinator,
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

        // Hydrate parent chain so downstream read tools (twig.tree) see a complete hierarchy
        var parentChainIds = new List<int>();
        if (item.ParentId.HasValue)
        {
            var chain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            if (chain.Count == 0)
            {
                // Parent not in cache — auto-fetch via resolver (best-effort)
                await activeItemResolver.ResolveByIdAsync(item.ParentId.Value, ct);
                chain = await workItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            }
            parentChainIds.AddRange(chain.Select(p => p.Id));
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

        // Best-effort sync — never fails the tool call
        try
        {
            await syncCoordinator.SyncItemSetAsync([item.Id, ..parentChainIds], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        await promptStateWriter.WritePromptStateAsync();

        var children = await workItemRepo.GetChildrenAsync(item.Id, ct);
        return McpResultBuilder.FormatWorkItemWithWorkingSet(item, parentChainIds.Count, children.Count);
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
