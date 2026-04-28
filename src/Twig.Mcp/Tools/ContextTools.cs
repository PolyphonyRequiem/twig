using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Workspace;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for context management: twig_set.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class ContextTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_set"), Description("Set the active work item by ID or title pattern")]
    public async Task<CallToolResult> Set(
        [Description("Work item ID (numeric) or title pattern (text)")] string idOrPattern,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrPattern))
            return McpResultBuilder.ToError("Usage: twig_set requires an ID or title pattern.");

        WorkspaceContext ctx;
        Domain.Aggregates.WorkItem item;

        if (int.TryParse(idOrPattern, out var id))
        {
            try { ctx = await resolver.ResolveForSetAsync(id, workspace, ct); }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException or WorkItemNotFoundException)
            { return McpResultBuilder.ToError(ex.Message); }

            var result = await ctx.ActiveItemResolver.ResolveByIdAsync(id, ct);

            if (result is ActiveItemResult.Unreachable u)
                return McpResultBuilder.ToError($"Work item #{u.Id} unreachable: {u.Reason}");

            item = result is ActiveItemResult.Found f
                ? f.WorkItem
                : ((ActiveItemResult.FetchedFromAdo)result).WorkItem;
        }
        else
        {
            try { ctx = resolver.Resolve(workspace); }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException)
            { return McpResultBuilder.ToError(ex.Message); }

            var matches = await ctx.WorkItemRepo.FindByPatternAsync(idOrPattern, ct);

            if (matches.Count == 0)
                return McpResultBuilder.ToError($"No cached items match '{idOrPattern}'.");

            if (matches.Count > 1)
            {
                var lines = matches.Select(m => $"  #{m.Id}: {m.Title} [{m.State}]");
                return McpResultBuilder.ToError(
                    $"Multiple matches — specify by ID:\n{string.Join("\n", lines)}");
            }

            item = matches[0];
            resolver.ActiveWorkspace = ctx.Key;
        }

        await ctx.ContextStore.SetActiveWorkItemIdAsync(item.Id, ct);

        // Extend working set around the target item (parent chain, 2 levels of children, links).
        // Best-effort — extension failures must never fail the tool call.
        try
        {
            await ctx.ContextChangeService.ExtendWorkingSetAsync(item.Id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        await ctx.PromptStateWriter.WritePromptStateAsync();

        // Compute working set summary for the response (post-extension snapshot)
        var parentChainCount = 0;
        if (item.ParentId.HasValue)
        {
            var chain = await ctx.WorkItemRepo.GetParentChainAsync(item.ParentId.Value, ct);
            parentChainCount = chain.Count;
        }
        var children = await ctx.WorkItemRepo.GetChildrenAsync(item.Id, ct);
        return McpResultBuilder.FormatWorkItemWithWorkingSet(item, parentChainCount, children.Count, ctx.Key.ToString());
    }
}