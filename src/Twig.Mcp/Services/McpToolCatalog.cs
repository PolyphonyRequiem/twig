using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Twig.Mcp.Services;

internal enum McpToolProfile
{
    Compact,
    Full,
}

/// <summary>
/// Canonical MCP tool catalog, visibility profiles, annotations, and wire-schema normalization.
/// Tool methods retain their broad compatibility signatures; this catalog controls what clients
/// initially see and adapts newer typed arguments back to legacy method parameters at invocation.
/// </summary>
internal static class McpToolCatalog
{
    public const string ProfileEnvironmentVariable = "TWIG_MCP_TOOL_PROFILE";

    public static readonly IReadOnlySet<string> AllToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_area",
        "twig_batch",
        "twig_cache_status",
        "twig_children",
        "twig_config",
        "twig_delete",
        "twig_discard",
        "twig_find_or_create",
        "twig_link",
        "twig_link_artifact",
        "twig_link_branch",
        "twig_list_workspaces",
        "twig_new",
        "twig_note",
        "twig_parent",
        "twig_patch",
        "twig_process",
        "twig_query",
        "twig_refresh",
        "twig_seed_chain",
        "twig_seed_discard",
        "twig_seed_edit",
        "twig_seed_link",
        "twig_seed_new",
        "twig_seed_publish",
        "twig_seed_reconcile",
        "twig_seed_validate",
        "twig_seed_view",
        "twig_set",
        "twig_show",
        "twig_sprint",
        "twig_state",
        "twig_sync",
        "twig_track",
        "twig_tracking_status",
        "twig_tree",
        "twig_untrack",
        "twig_update",
        "twig_verify_descendants",
        "twig_workspace",
    };

    /// <summary>
    /// High-frequency surface advertised by default. Hidden tools remain callable by name and the
    /// full catalog is available with <c>TWIG_MCP_TOOL_PROFILE=full</c> or <c>--tool-profile full</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> CompactToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_cache_status",
        "twig_find_or_create",
        "twig_note",
        "twig_query",
        "twig_set",
        "twig_show",
        "twig_state",
        "twig_sync",
        "twig_update",
        "twig_workspace",
    };

    public static readonly IReadOnlySet<string> BatchableToolNames =
        new HashSet<string>(AllToolNames.Where(name => name != "twig_batch"), StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ReadOnlyToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_area",
        "twig_cache_status",
        "twig_children",
        "twig_config",
        "twig_list_workspaces",
        "twig_parent",
        "twig_process",
        "twig_query",
        "twig_seed_validate",
        "twig_seed_view",
        "twig_show",
        "twig_sprint",
        "twig_tracking_status",
        "twig_tree",
        "twig_verify_descendants",
        "twig_workspace",
    };

    private static readonly IReadOnlySet<string> DestructiveToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_batch",
        "twig_delete",
        "twig_discard",
        "twig_patch",
        "twig_seed_discard",
        "twig_seed_edit",
        "twig_seed_publish",
        "twig_seed_reconcile",
        "twig_state",
        "twig_sync",
        "twig_untrack",
        "twig_update",
    };

    private static readonly IReadOnlySet<string> IdempotentToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_delete",
        "twig_discard",
        "twig_link_artifact",
        "twig_link_branch",
        "twig_seed_discard",
        "twig_seed_edit",
        "twig_seed_reconcile",
        "twig_track",
        "twig_untrack",
    };

    private static readonly IReadOnlySet<string> ClosedWorldToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "twig_cache_status",
        "twig_config",
        "twig_list_workspaces",
        "twig_process",
        "twig_seed_discard",
        "twig_seed_edit",
        "twig_seed_link",
        "twig_seed_reconcile",
        "twig_seed_validate",
        "twig_seed_view",
        "twig_tracking_status",
        "twig_untrack",
    };

    public static McpToolProfile ResolveProfile(string[] args, string? environmentValue)
    {
        string? value = null;
        for (var i = 0; i < args.Length; i++)
        {
            const string prefix = "--tool-profile=";
            if (args[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = args[i][prefix.Length..];
                break;
            }

            if (string.Equals(args[i], "--tool-profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                value = args[i + 1];
                break;
            }
        }

        value ??= environmentValue;
        if (string.IsNullOrWhiteSpace(value)) return McpToolProfile.Compact;

        return value.Trim().ToLowerInvariant() switch
        {
            "compact" or "core" => McpToolProfile.Compact,
            "full" or "all" => McpToolProfile.Full,
            _ => throw new ArgumentException(
                $"Unknown MCP tool profile '{value}'. Valid profiles: compact, full."),
        };
    }

    public static ListToolsResult FilterList(
        ListToolsResult source,
        McpToolProfile profile,
        bool exposeWorkspaceOverride)
    {
        var tools = source.Tools
            .Where(tool => IsVisible(tool.Name, profile))
            .Select(tool => NormalizeTool(tool, exposeWorkspaceOverride))
            .ToList();

        return new ListToolsResult
        {
            Tools = tools,
            NextCursor = source.NextCursor,
        };
    }

    public static void RewriteStructuredArguments(CallToolRequestParams request)
    {
        var args = request.Arguments;
        if (args is null) return;

        switch (request.Name)
        {
            case "twig_batch":
                RewriteAsJsonString(args, "graph");
                break;
            case "twig_patch":
                RewriteAsJsonString(args, "fields");
                break;
            case "twig_track":
            case "twig_untrack":
                RewriteTrackingIds(args);
                break;
        }
    }

    private static bool IsVisible(string name, McpToolProfile profile) =>
        profile == McpToolProfile.Full || CompactToolNames.Contains(name);

    private static Tool NormalizeTool(Tool source, bool exposeWorkspaceOverride)
    {
        var schema = JsonNode.Parse(source.InputSchema.GetRawText())!.AsObject();
        var properties = schema["properties"] as JsonObject;
        if (properties is not null)
        {
            properties.Remove("verbose");
            RemoveRequired(schema, "verbose");
            if (!exposeWorkspaceOverride)
            {
                properties.Remove("workspace");
                RemoveRequired(schema, "workspace");
            }

            foreach (var (_, value) in properties)
            {
                if (value is JsonObject property) NormalizeOptionalProperty(property);
            }

            ApplyTypedSchemas(source.Name, properties);
            ApplyConstraints(source.Name, properties);
        }

        schema["additionalProperties"] = false;
        using var schemaDocument = JsonDocument.Parse(schema.ToJsonString());

        return new Tool
        {
            Name = source.Name,
            Title = source.Title,
            Description = source.Description,
            InputSchema = schemaDocument.RootElement.Clone(),
            OutputSchema = source.OutputSchema,
            Annotations = BuildAnnotations(source.Name),
#pragma warning disable MCPEXP001 // Preserve SDK-generated task-support metadata on cloned discovery tools.
            Execution = source.Execution,
#pragma warning restore MCPEXP001
            Icons = source.Icons,
            Meta = source.Meta,
        };
    }

    private static void RemoveRequired(JsonObject schema, string propertyName)
    {
        if (schema["required"] is not JsonArray required) return;
        var node = required.FirstOrDefault(value => value?.GetValue<string>() == propertyName);
        if (node is not null) required.Remove(node);
    }

    private static void NormalizeOptionalProperty(JsonObject property)
    {
        if (property["default"] is null) property.Remove("default");

        if (property["type"] is not JsonArray types ||
            !types.Any(node => node?.GetValue<string>() == "null"))
        {
            return;
        }

        var nonNullTypes = types
            .Where(node => node?.GetValue<string>() != "null")
            .Select(node => node!.DeepClone())
            .ToList();
        property["type"] = nonNullTypes.Count == 1
            ? nonNullTypes[0]
            : new JsonArray(nonNullTypes.ToArray());
    }

    private static void ApplyTypedSchemas(string toolName, JsonObject properties)
    {
        if (toolName == "twig_batch" && properties["graph"] is JsonObject graph)
        {
            properties["graph"] = new JsonObject
            {
                ["description"] = graph["description"]?.DeepClone(),
                ["type"] = "object",
                ["additionalProperties"] = true,
            };
        }

        if (toolName == "twig_patch" && properties["fields"] is JsonObject fields)
        {
            properties["fields"] = new JsonObject
            {
                ["description"] = fields["description"]?.DeepClone(),
                ["type"] = "object",
                ["minProperties"] = 1,
                ["additionalProperties"] = new JsonObject
                {
                    ["type"] = "string",
                },
            };
        }

        if (toolName is "twig_track" or "twig_untrack" && properties["id"] is JsonObject ids)
        {
            properties["id"] = new JsonObject
            {
                ["description"] = ids["description"]?.DeepClone(),
                ["oneOf"] = new JsonArray(
                    new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["minItems"] = 1,
                        ["uniqueItems"] = true,
                    }),
            };
        }
    }

    private static void ApplyConstraints(string toolName, JsonObject properties)
    {
        if (properties["workspace"] is JsonObject workspace)
            workspace["pattern"] = "^[^/\\s]+/[^/\\s]+$";

        if (properties["format"] is JsonObject format)
            format["enum"] = new JsonArray("markdown", "raw");

        if (toolName == "twig_link" && properties["linkType"] is JsonObject linkType)
            linkType["enum"] = new JsonArray("parent", "child", "related", "predecessor", "successor");

        if (toolName == "twig_seed_link" && properties["type"] is JsonObject seedLinkType)
        {
            seedLinkType["enum"] = new JsonArray(
                "blocks", "blocked-by", "depends-on", "depended-on-by",
                "successor", "predecessor", "related", "parent-child");
        }

        switch (toolName)
        {
            case "twig_query":
                SetMinimum(properties, "top", 1);
                SetMinimum(properties, "createdSince", 0);
                SetMinimum(properties, "changedSince", 0);
                break;
            case "twig_show":
            case "twig_tree":
                SetMinimum(properties, "depth", 0);
                break;
            case "twig_verify_descendants":
                SetMinimum(properties, "maxDepth", 0);
                break;
            case "twig_batch":
                SetMinimum(properties, "timeoutSeconds", 1);
                SetMaximum(properties, "timeoutSeconds", 300);
                break;
            case "twig_link":
                SetMinimum(properties, "sourceId", 1);
                SetMinimum(properties, "targetId", 1);
                break;
            case "twig_link_branch":
            case "twig_link_artifact":
                SetMinimum(properties, "workItemId", 1);
                break;
            case "twig_delete":
                SetMinimum(properties, "id", 1);
                break;
            case "twig_seed_publish":
            case "twig_seed_validate":
            case "twig_seed_discard":
            case "twig_seed_edit":
                SetMaximum(properties, "id", -1);
                break;
            case "twig_seed_new":
                DenyInteger(properties, "parentId", 0);
                break;
            case "twig_seed_chain":
                DenyInteger(properties, "parentId", 0);
                SetMinimum(properties, "titles", 1, "minItems");
                break;
        }

        if (toolName is "twig_new" or "twig_find_or_create" or "twig_seed_new" or "twig_seed_edit")
            SetMinimum(properties, "title", 1, "minLength");
        if (toolName == "twig_note")
            SetMinimum(properties, "text", 1, "minLength");
    }

    private static void SetMinimum(
        JsonObject properties,
        string propertyName,
        int value,
        string keyword = "minimum")
    {
        if (properties[propertyName] is JsonObject property) property[keyword] = value;
    }

    private static void SetMaximum(JsonObject properties, string propertyName, int value)
    {
        if (properties[propertyName] is JsonObject property) property["maximum"] = value;
    }

    private static void DenyInteger(JsonObject properties, string propertyName, int value)
    {
        if (properties[propertyName] is JsonObject property)
        {
            property["not"] = new JsonObject
            {
                ["enum"] = new JsonArray(value),
            };
        }
    }

    private static ToolAnnotations BuildAnnotations(string name)
    {
        var readOnly = ReadOnlyToolNames.Contains(name);
        return new ToolAnnotations
        {
            ReadOnlyHint = readOnly,
            DestructiveHint = !readOnly && DestructiveToolNames.Contains(name),
            IdempotentHint = readOnly || IdempotentToolNames.Contains(name),
            OpenWorldHint = !ClosedWorldToolNames.Contains(name),
        };
    }

    private static void RewriteAsJsonString(IDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.String) return;
        args[name] = ToJsonStringElement(value.GetRawText());
    }

    private static void RewriteTrackingIds(IDictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("id", out var value) || value.ValueKind == JsonValueKind.String) return;

        var text = value.ValueKind == JsonValueKind.Array
            ? value.GetRawText()
            : value.ToString();
        args["id"] = ToJsonStringElement(text);
    }

    private static JsonElement ToJsonStringElement(string value)
    {
        var encoded = JsonEncodedText.Encode(value);
        using var document = JsonDocument.Parse($"\"{encoded}\"");
        return document.RootElement.Clone();
    }
}
