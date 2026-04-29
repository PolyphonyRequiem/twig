using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tool for process discovery: <c>twig_process</c>.
/// Lists all work item types (no args) or shows type details (with type name).
/// </summary>
[McpServerToolType]
public sealed class ProcessTools(WorkspaceResolver resolver)
{
    [McpServerTool(Name = "twig_process"), Description("Show process configuration: list types (no args) or type details (with type name)")]
    public async Task<CallToolResult> Process(
        [Description("Work item type name to show details for (omit to list all types)")] string? type = null,
        [Description("Target workspace (format: \"org/project\"). When omitted, inferred from context or single-workspace default.")] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        CallToolResult toolResult;
        if (type is null)
            toolResult = await ListTypesAsync(ctx, ct);
        else
            toolResult = await ShowTypeDetailAsync(ctx, type, ct);

        return await EnvelopeBuilder.WrapAsync(ctx, toolResult, verbose, ct);
    }

    private static async Task<CallToolResult> ListTypesAsync(WorkspaceContext ctx, CancellationToken ct)
    {
        var types = await ctx.ProcessTypeStore.GetAllAsync(ct);

        if (types.Count == 0)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.CacheStale, "No process types found. Use twig_sync to refresh process data.", ctx, ct);

        return McpResultBuilder.FormatProcessList(types);
    }

    private static async Task<CallToolResult> ShowTypeDetailAsync(
        WorkspaceContext ctx, string typeName, CancellationToken ct)
    {
        var typeRecord = await ctx.ProcessTypeStore.GetByNameAsync(typeName, ct);

        if (typeRecord is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Type '{typeName}' not found. Use twig_sync to refresh process data.", ctx, ct);

        var fields = await ctx.FieldDefinitionStore.GetAllAsync(ct);
        return McpResultBuilder.FormatProcessType(typeRecord, fields);
    }
}
