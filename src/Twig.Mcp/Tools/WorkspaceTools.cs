using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for workspace management: twig_list_workspaces.
/// </summary>
[McpServerToolType]
public sealed class WorkspaceTools(IWorkspaceRegistry registry, WorkspaceResolver resolver)
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    [McpServerTool(Name = "twig_list_workspaces"), Description("List all registered workspaces discovered from .twig/ directory layout")]
    public CallToolResult ListWorkspaces()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        writer.WriteStartArray("workspaces");
        var activeKey = resolver.ActiveWorkspace;
        foreach (var key in registry.Workspaces)
        {
            writer.WriteStartObject();
            writer.WriteString("org", key.Org);
            writer.WriteString("project", key.Project);
            writer.WriteString("key", key.ToString());
            writer.WriteBoolean("isActive", key == activeKey);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("count", registry.Workspaces.Count);
        writer.WriteBoolean("isSingleWorkspace", registry.IsSingleWorkspace);

        writer.WriteEndObject();
        writer.Flush();

        return McpResultBuilder.ToResult(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
