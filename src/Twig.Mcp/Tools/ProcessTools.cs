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
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return McpResultBuilder.ToError(err!);

        if (type is null)
            return await ListTypesAsync(ctx, ct);

        return await ShowTypeDetailAsync(ctx, type, ct);
    }

    private static async Task<CallToolResult> ListTypesAsync(WorkspaceContext ctx, CancellationToken ct)
    {
        var types = await ctx.ProcessTypeStore.GetAllAsync(ct);

        if (types.Count == 0)
            return McpResultBuilder.ToError("No process types found. Use twig_sync to refresh process data.");

        return McpResultBuilder.FormatProcessList(types);
    }

    private static async Task<CallToolResult> ShowTypeDetailAsync(
        WorkspaceContext ctx, string typeName, CancellationToken ct)
    {
        var typeRecord = await ctx.ProcessTypeStore.GetByNameAsync(typeName, ct);

        if (typeRecord is null)
            return McpResultBuilder.ToError($"Type '{typeName}' not found. Use twig_sync to refresh process data.");

        var fields = await ctx.FieldDefinitionStore.GetAllAsync(ct);
        return McpResultBuilder.FormatProcessType(typeRecord, fields);
    }
}
