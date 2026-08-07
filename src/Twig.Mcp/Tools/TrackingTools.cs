using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Config;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Enums;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for manual work-item tracking: twig_track, twig_untrack, twig_tracking_status.
/// Resolves per-workspace services via <see cref="ConnectionResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class TrackingTools(ConnectionResolver resolver)
{
    [McpServerTool(Name = "twig_track"), Description("Track one or more work items by ID. Tracked items are included in every ADO sync/refresh.")]
    public async Task<CallToolResult> Track(
        [Description("Work item ID (integer) or JSON array of IDs (e.g. [1,2,3])")] string id,
        [Description("When true, also tracks all descendant work items")] bool recursive = false,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_track requires at least one work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.Get<ITrackingRepository>() is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                "Tracking is not available for this workspace.", ctx, ct);

        var ids = ParseIds(id);
        if (ids.Count == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Could not parse any valid work item IDs from the provided input.");

        // ADO #145: pinning goes through the shared mutation-workflow seam, so this surface and
        // the CLI cannot disagree about what a pin means.
        //
        // 🔴 A recursive pin no longer WALKS the tree and pins each descendant it finds. That
        // implementation captured the subtree as it stood at pin time, so a child created
        // afterwards was never on the Bench — the exact defect the spec singles out. One subtree
        // selector is stored instead, and the descendants are matched live on every look.
        // Consequently trackedIds/trackedCount now report what was PINNED (the roots), not a
        // snapshot of what that pin currently expands to.
        var trackedIds = new List<int>();

        foreach (var workItemId in ids)
        {
            await ctx.Get<PinWorkflow>().PinAsync(workItemId, includeSubtree: recursive, ct);
            trackedIds.Add(workItemId);
        }

        var uniqueCount = trackedIds.Distinct().Count();

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("trackedCount", uniqueCount);
            writer.WriteBoolean("recursive", recursive);

            writer.WriteStartArray("trackedIds");
            foreach (var tid in trackedIds.Distinct().Order())
                writer.WriteNumberValue(tid);
            writer.WriteEndArray();
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_untrack"), Description("Stop tracking one or more work items by ID. No error if the item is not currently tracked.")]
    public async Task<CallToolResult> Untrack(
        [Description("Work item ID (integer) or JSON array of IDs (e.g. [1,2,3])")] string id,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_untrack requires at least one work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.Get<ITrackingRepository>() is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                "Tracking is not available for this workspace.", ctx, ct);

        var ids = ParseIds(id);
        if (ids.Count == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Could not parse any valid work item IDs from the provided input.");

        var removedIds = new List<int>();
        foreach (var workItemId in ids)
        {
            await ctx.Get<PinWorkflow>().UnpinAsync(workItemId, ct);
            removedIds.Add(workItemId);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteNumber("untrackedCount", removedIds.Count);

            writer.WriteStartArray("untrackedIds");
            foreach (var uid in removedIds.Distinct().Order())
                writer.WriteNumberValue(uid);
            writer.WriteEndArray();
        }, verbose, ct);
    }

    [McpServerTool(Name = "twig_tracking_status"), Description("List all currently tracked work items with their title, type, tracking mode, and last refreshed time. No network call — reads from local cache only.")]
    public async Task<CallToolResult> TrackingStatus(
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.Get<ITrackingRepository>() is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                "Tracking is not available for this workspace.", ctx, ct);

        var tracked = await ctx.Get<ITrackingRepository>().GetAllTrackedAsync(ct);

        // Join with work item cache to get title, type, and ChangedDate
        var ids = tracked.Select(t => t.WorkItemId).ToList();
        var workItems = ids.Count > 0
            ? await ctx.Get<IWorkItemRepository>().GetByIdsAsync(ids, ct)
            : [];
        var workItemLookup = workItems.ToDictionary(w => w.Id);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteStartArray("trackedItems");
            foreach (var item in tracked)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", item.WorkItemId);

                if (workItemLookup.TryGetValue(item.WorkItemId, out var wi))
                {
                    writer.WriteString("title", wi.Title);
                    writer.WriteString("type", wi.Type.ToString());
                }
                else
                {
                    writer.WriteString("title", "");
                    writer.WriteString("type", "");
                }

                writer.WriteBoolean("recursive", item.Mode == TrackingMode.Tree);
                writer.WriteString("trackedSince", item.TrackedAt.ToString("o"));

                // lastRefreshed: ChangedDate from the work item cache (proxy for last sync time)
                var lastRefreshed = "";
                if (wi is not null && wi.Fields.TryGetValue("System.ChangedDate", out var changedDate)
                    && !string.IsNullOrEmpty(changedDate))
                {
                    lastRefreshed = changedDate;
                }

                writer.WriteString("lastRefreshed", lastRefreshed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("totalCount", tracked.Count);
        }, verbose, ct);
    }

    /// <summary>
    /// Parses the <paramref name="input"/> as either a single integer or a JSON array of integers.
    /// Also supports comma-separated values (e.g. "1,2,3").
    /// </summary>
    internal static List<int> ParseIds(string input)
    {
        var trimmed = input.Trim();
        var result = new List<int>();

        // Single integer
        if (int.TryParse(trimmed, out var singleId))
        {
            result.Add(singleId);
            return result;
        }

        // JSON array: [1, 2, 3]
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var arrayId))
                            result.Add(arrayId);
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through — invalid JSON
            }

            return result;
        }

        // Comma-separated: "1,2,3"
        foreach (var part in trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out var partId))
                result.Add(partId);
        }

        return result;
    }
}
