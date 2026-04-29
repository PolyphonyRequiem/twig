using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for configuration and admin queries: <c>twig_area</c>.
/// Returns project structure information from ADO with local caching.
/// </summary>
[McpServerToolType]
public sealed class AdminTools(WorkspaceResolver resolver)
{
    private const string AreaTreeJsonKey = "area_tree_json";
    private const string AreaTreeFetchedAtKey = "area_tree_fetched_at";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    [McpServerTool(Name = "twig_area"), Description("Show the project area path classification tree. Cached locally; refreshes from ADO when stale (>1 hour). Read-only — does not modify area paths.")]
    public async Task<CallToolResult> Area(
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // 1. Check cache
        var cachedJson = await ctx.ContextStore.GetValueAsync(AreaTreeJsonKey, ct);
        var cachedAtStr = await ctx.ContextStore.GetValueAsync(AreaTreeFetchedAtKey, ct);
        var isCacheFresh = IsCacheFresh(cachedAtStr);

        if (isCacheFresh && cachedJson is not null)
        {
            // Cache hit — return directly
            return await BuildAreaResultFromCacheAsync(ctx, cachedJson, cachedAtStr!, verbose, ct);
        }

        // 2. Cache miss or stale — try fetching from ADO
        try
        {
            var areaTree = await ctx.IterationService.GetAreaTreeAsync(ct);
            var json = SerializeAreaTree(areaTree);
            var fetchedAt = DateTimeOffset.UtcNow.ToString("o");

            // Update cache
            await ctx.ContextStore.SetValueAsync(AreaTreeJsonKey, json, ct);
            await ctx.ContextStore.SetValueAsync(AreaTreeFetchedAtKey, fetchedAt, ct);

            return await BuildAreaResultAsync(ctx, areaTree, fetchedAt, verbose, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 3. ADO unreachable — return stale cache if available
            if (cachedJson is not null)
                return await BuildAreaResultFromCacheAsync(ctx, cachedJson, cachedAtStr ?? "", verbose, ct);

            // No cache at all
            return await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.AdoUnreachable,
                "Cannot fetch area tree from ADO and no cached data is available. Try again when connected.",
                ctx, ct);
        }
    }

    private static bool IsCacheFresh(string? fetchedAtStr)
    {
        if (string.IsNullOrEmpty(fetchedAtStr))
            return false;

        if (!DateTimeOffset.TryParse(fetchedAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var fetchedAt))
            return false;

        return DateTimeOffset.UtcNow - fetchedAt < CacheTtl;
    }

    private static string SerializeAreaTree(AreaTreeNode node)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        WriteAreaNode(writer, node);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteAreaNode(Utf8JsonWriter writer, AreaTreeNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        writer.WriteString("path", node.Path);
        writer.WriteStartArray("children");
        foreach (var child in node.Children)
            WriteAreaNode(writer, child);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static async Task<CallToolResult> BuildAreaResultAsync(
        WorkspaceContext ctx, AreaTreeNode tree, string fetchedAt, bool verbose, CancellationToken ct)
    {
        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteStartArray("areas");
            WriteAreaNodeForOutput(writer, tree);
            writer.WriteEndArray();
            writer.WriteString("fetchedAt", fetchedAt);
            writer.WriteBoolean("fromCache", false);
        }, verbose, ct);
    }

    private static async Task<CallToolResult> BuildAreaResultFromCacheAsync(
        WorkspaceContext ctx, string cachedJson, string fetchedAt, bool verbose, CancellationToken ct)
    {
        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteStartArray("areas");

            using var doc = JsonDocument.Parse(cachedJson);
            var root = doc.RootElement;
            // The cached JSON is a single node; write it as the first (root) area
            root.WriteTo(writer);

            writer.WriteEndArray();
            writer.WriteString("fetchedAt", fetchedAt);
            writer.WriteBoolean("fromCache", true);
        }, verbose, ct);
    }

    private static void WriteAreaNodeForOutput(Utf8JsonWriter writer, AreaTreeNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        writer.WriteString("path", node.Path);
        writer.WriteStartArray("children");
        foreach (var child in node.Children)
            WriteAreaNodeForOutput(writer, child);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
