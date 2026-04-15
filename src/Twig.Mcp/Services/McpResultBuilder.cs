using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;

namespace Twig.Mcp.Services;

internal static class McpResultBuilder
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public static CallToolResult ToResult(string json) =>
        new() { Content = [new TextContentBlock { Text = json }] };

    public static CallToolResult ToError(string message) =>
        new() { Content = [new TextContentBlock { Text = message }], IsError = true };

    public static CallToolResult FormatWorkItem(WorkItem item) =>
        BuildJson(writer =>
        {
            WriteWorkItemCore(writer, item);
            writer.WriteString("areaPath", item.AreaPath.ToString());
            writer.WriteString("iterationPath", item.IterationPath.ToString());
        });

    public static CallToolResult FormatStatus(StatusSnapshot snapshot) =>
        BuildJson(writer =>
        {
        writer.WriteBoolean("hasContext", snapshot.HasContext);

        if (snapshot.Item is not null)
        {
            writer.WritePropertyName("item");
            writer.WriteStartObject();
            WriteWorkItemCore(writer, snapshot.Item);
            writer.WriteString("areaPath", snapshot.Item.AreaPath.ToString());
            writer.WriteString("iterationPath", snapshot.Item.IterationPath.ToString());
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("item");
        }

        // Pending changes
        writer.WriteStartArray("pendingChanges");
        foreach (var change in snapshot.PendingChanges)
        {
            writer.WriteStartObject();
            writer.WriteNumber("workItemId", change.WorkItemId);
            writer.WriteString("changeType", change.ChangeType);
            writer.WriteString("fieldName", change.FieldName);
            writer.WriteString("oldValue", change.OldValue);
            writer.WriteString("newValue", change.NewValue);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in snapshot.Seeds)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, seed);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Error state
        if (snapshot.UnreachableId.HasValue)
        {
            writer.WriteNumber("unreachableId", snapshot.UnreachableId.Value);
            writer.WriteString("unreachableReason", snapshot.UnreachableReason);
        }

        });

    public static CallToolResult FormatTree(WorkTree tree, int totalChildren) =>
        BuildJson(writer =>
        {
        // Focus
        writer.WritePropertyName("focus");
        writer.WriteStartObject();
        WriteWorkItemCore(writer, tree.FocusedItem);
        writer.WriteEndObject();

        // Parent chain
        writer.WriteStartArray("parentChain");
        foreach (var parent in tree.ParentChain)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, parent);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Children
        writer.WriteStartArray("children");
        foreach (var child in tree.Children)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, child);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("totalChildren", totalChildren);

        // Sibling counts
        if (tree.SiblingCounts is { Count: > 0 })
        {
            writer.WriteStartObject("siblingCounts");
            foreach (var (id, count) in tree.SiblingCounts)
            {
                if (count.HasValue)
                    writer.WriteNumber(id.ToString(), count.Value);
                else
                    writer.WriteNull(id.ToString());
            }
            writer.WriteEndObject();
        }

        // Links
        writer.WriteStartArray("links");
        foreach (var link in tree.FocusedItemLinks)
        {
            WriteLinkObject(writer, link);
        }
        writer.WriteEndArray();

        });

    public static CallToolResult FormatWorkspace(Workspace workspace, int staleDays) =>
        BuildJson(writer =>
        {
        // Context
        writer.WritePropertyName("context");
        if (workspace.ContextItem is not null)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, workspace.ContextItem);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        // Sprint items
        writer.WriteStartArray("sprintItems");
        foreach (var item in workspace.SprintItems)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, item);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in workspace.Seeds)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, seed);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        // Stale seeds
        var staleSeeds = workspace.GetStaleSeeds(staleDays);
        writer.WriteStartArray("staleSeeds");
        foreach (var s in staleSeeds)
        {
            writer.WriteNumberValue(s.Id);
        }
        writer.WriteEndArray();

        // Dirty count
        var dirtyItems = workspace.GetDirtyItems();
        writer.WriteNumber("dirtyCount", dirtyItems.Count);
        });

    public static CallToolResult FormatFlushSummary(McpFlushSummary summary)
    {
        var json = JsonSerializer.Serialize(summary, McpJsonContext.Default.McpFlushSummary);
        return ToResult(json);
    }

    private static CallToolResult BuildJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        write(writer);
        writer.WriteEndObject();
        writer.Flush();
        return ToResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteWorkItemCore(Utf8JsonWriter writer, WorkItem item)
    {
        writer.WriteNumber("id", item.Id);
        writer.WriteString("title", item.Title);
        writer.WriteString("type", item.Type.ToString());
        writer.WriteString("state", item.State);
        writer.WriteString("assignedTo", item.AssignedTo);
        writer.WriteBoolean("isDirty", item.IsDirty);
        writer.WriteBoolean("isSeed", item.IsSeed);
        if (item.ParentId.HasValue)
            writer.WriteNumber("parentId", item.ParentId.Value);
        else
            writer.WriteNull("parentId");
    }

    private static void WriteLinkObject(Utf8JsonWriter writer, WorkItemLink link)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sourceId", link.SourceId);
        writer.WriteNumber("targetId", link.TargetId);
        writer.WriteString("linkType", link.LinkType);
        writer.WriteEndObject();
    }
}

public sealed record McpFlushSummary
{
    public int Flushed { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<McpFlushItemFailure> Failures { get; init; } = [];
}

public sealed record McpFlushItemFailure
{
    public int WorkItemId { get; init; }
    public string Reason { get; init; } = string.Empty;
}

[JsonSerializable(typeof(McpFlushSummary))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class McpJsonContext : JsonSerializerContext;
