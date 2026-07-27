using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Config;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Workspace;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for context management: twig_set.
/// Resolves per-workspace services via <see cref="ConnectionResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class ContextTools(ConnectionResolver resolver)
{
    [McpServerTool(Name = "twig_set"), Description("Set the active work item by ID or title pattern")]
    public async Task<CallToolResult> Set(
        [Description("Work item ID (numeric) or title pattern (text)")] string idOrPattern,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idOrPattern))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_set requires an ID or title pattern.");

        ConnectionScope ctx;
        Domain.Aggregates.WorkItem item;

        if (int.TryParse(idOrPattern, out var id))
        {
            try { ctx = await resolver.ResolveForSetAsync(id, workspace, ct); }
            catch (WorkItemNotFoundException ex)
            { return EnvelopeBuilder.Error(McpErrorCode.ItemNotFound, ex.Message); }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException)
            { return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, ex.Message); }

            var result = await ctx.Get<ActiveItemResolver>().ResolveByIdAsync(id, ct);

            if (result is ActiveUnreachable u)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Work item #{u.Id} unreachable: {u.Reason}", ctx, ct);

            item = result switch
            {
                Found f => f.WorkItem,
                FetchedFromAdo a => a.WorkItem,
                _ => throw new InvalidOperationException("Unexpected active item result"),
            };
        }
        else
        {
            try { ctx = resolver.Resolve(workspace); }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or AmbiguousWorkspaceException)
            { return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, ex.Message); }

            var matches = await ctx.Get<IWorkItemRepository>().FindByPatternAsync(idOrPattern, ct);

            if (matches.Count == 0)
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"No cached items match '{idOrPattern}'.", ctx, ct);

            if (matches.Count > 1)
            {
                var lines = matches.Select(m => $"  #{m.Id}: {m.Title} [{m.State}]");
                return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                    $"Multiple matches — specify by ID:\n{string.Join("\n", lines)}", ctx, ct);
            }

            item = matches[0];
            resolver.ActiveWorkspace = ctx.Connection;
        }

        await ctx.Get<IContextStore>().SetActiveWorkItemIdAsync(item.Id, ct);

        // Extend working set around the target item (parent chain, 2 levels of children, links).
        // Best-effort — extension failures must never fail the tool call.
        try
        {
            await ctx.Get<ContextChangeService>().ExtendWorkingSetAsync(item.Id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }

        await ctx.Get<IPromptStateWriter>().WritePromptStateAsync();

        // Compute working set summary for the response (post-extension snapshot)
        var parentChainCount = 0;
        if (item.ParentId.HasValue)
        {
            var chain = await ctx.Get<IWorkItemRepository>().GetParentChainAsync(item.ParentId.Value, ct);
            parentChainCount = chain.Count;
        }
        var children = await ctx.Get<IWorkItemRepository>().GetChildrenAsync(item.Id, ct);
        var toolResult = McpResultBuilder.FormatWorkItemWithWorkingSet(item, parentChainCount, children.Count, ctx.Connection.ToString());
        return await EnvelopeBuilder.WrapAsync(ctx, toolResult, verbose, ct);
    }
}