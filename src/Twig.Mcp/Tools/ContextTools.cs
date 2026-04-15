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
    IPromptStateWriter promptStateWriter)
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

        // Best-effort sync — never fails the tool call
        try
        {
            await syncCoordinator.SyncItemSetAsync([item.Id], ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        await promptStateWriter.WritePromptStateAsync();

        return McpResultBuilder.FormatWorkItem(item);
    }
}
