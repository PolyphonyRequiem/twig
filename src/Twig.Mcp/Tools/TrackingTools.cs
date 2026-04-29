using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Enums;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for manual work-item tracking: twig_track, twig_untrack.
/// Resolves per-workspace services via <see cref="WorkspaceResolver"/>.
/// </summary>
[McpServerToolType]
public sealed class TrackingTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_track"), Description("Track one or more work items by ID. Tracked items are included in every ADO sync/refresh.")]
    public async Task<CallToolResult> Track(
        [Description("Work item ID (integer) or JSON array of IDs (e.g. [1,2,3])")] string id,
        [Description("When true, also tracks all descendant work items")] bool recursive = false,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_track requires at least one work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.TrackingRepo is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                "Tracking is not available for this workspace.", ctx, ct);

        var ids = ParseIds(id);
        if (ids.Count == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Could not parse any valid work item IDs from the provided input.");

        var mode = recursive ? TrackingMode.Tree : TrackingMode.Single;
        var trackedIds = new List<int>();

        foreach (var workItemId in ids)
        {
            await ctx.TrackingRepo.UpsertTrackedAsync(workItemId, mode, ct);
            trackedIds.Add(workItemId);

            if (recursive)
            {
                var descendantIds = await ResolveDescendantsAsync(ctx, workItemId, ct);
                foreach (var descId in descendantIds)
                {
                    await ctx.TrackingRepo.UpsertTrackedAsync(descId, TrackingMode.Single, ct);
                    trackedIds.Add(descId);
                }
            }
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
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Usage: twig_untrack requires at least one work item ID.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        if (ctx.TrackingRepo is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.InvalidInput,
                "Tracking is not available for this workspace.", ctx, ct);

        var ids = ParseIds(id);
        if (ids.Count == 0)
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "Could not parse any valid work item IDs from the provided input.");

        var removedIds = new List<int>();
        foreach (var workItemId in ids)
        {
            await ctx.TrackingRepo.RemoveTrackedAsync(workItemId, ct);
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

    /// <summary>
    /// Recursively resolves all descendant work item IDs for a given parent.
    /// Uses cache-first with ADO fallback via <see cref="WorkspaceContext.FetchChildrenWithFallbackAsync"/>.
    /// </summary>
    private static async Task<List<int>> ResolveDescendantsAsync(
        WorkspaceContext ctx, int parentId, CancellationToken ct)
    {
        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(parentId);
        var visited = new HashSet<int> { parentId };

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var children = await ctx.FetchChildrenWithFallbackAsync(currentId, ct);

            foreach (var child in children)
            {
                if (visited.Add(child.Id))
                {
                    result.Add(child.Id);
                    queue.Enqueue(child.Id);
                }
            }
        }

        return result;
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
