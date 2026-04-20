using System.Text;
using System.Text.Json;
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

    public static CallToolResult FormatWorkItemWithWorkingSet(
        WorkItem item, int parentChainCount, int childCount, string? workspace = null) =>
        BuildJson(writer =>
        {
            WriteWorkItemCore(writer, item);
            writer.WriteString("areaPath", item.AreaPath.ToString());
            writer.WriteString("iterationPath", item.IterationPath.ToString());
            writer.WriteStartObject("workingSet");
            writer.WriteNumber("parentChainCount", parentChainCount);
            writer.WriteNumber("childCount", childCount);
            writer.WriteEndObject();
            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatStatus(StatusSnapshot snapshot, string? workspace = null) =>
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

            WriteWorkItemArray(writer, "seeds", snapshot.Seeds);

            // Error state
            if (snapshot.UnreachableId.HasValue)
            {
                writer.WriteNumber("unreachableId", snapshot.UnreachableId.Value);
                writer.WriteString("unreachableReason", snapshot.UnreachableReason);
            }

            WriteOptionalWorkspace(writer, workspace);
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
            WriteWorkItemArray(writer, "parentChain", tree.ParentChain);

            // Children
            WriteWorkItemArray(writer, "children", tree.Children);

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

    public static CallToolResult FormatWorkspace(Workspace workspace, int staleDays, string? workspaceKey = null) =>
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
            WriteWorkItemArray(writer, "sprintItems", workspace.SprintItems);

            WriteWorkItemArray(writer, "seeds", workspace.Seeds);

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

            WriteOptionalWorkspace(writer, workspaceKey);
        });

    public static CallToolResult FormatStateChange(WorkItem updated, string previousState) =>
        BuildJson(writer =>
        {
            WriteWorkItemCore(writer, updated);
            writer.WriteString("previousState", previousState);
        });

    public static CallToolResult FormatFieldUpdate(WorkItem updated, string field, string displayValue) =>
        BuildJson(writer =>
        {
            WriteWorkItemCore(writer, updated);
            var truncated = displayValue.Length > 100
                ? string.Concat(displayValue.AsSpan(0, 100), "...")
                : displayValue;
            writer.WriteString("updatedField", field);
            writer.WriteString("updatedValue", truncated);
        });

    public static CallToolResult FormatNoteAdded(int itemId, string title, bool isPending) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("id", itemId);
            writer.WriteString("title", title);
            writer.WriteBoolean("noteAdded", true);
            writer.WriteBoolean("isPending", isPending);
        });

    public static CallToolResult FormatDiscardNone(int id, string title) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("title", title);
            writer.WriteBoolean("discarded", false);
            writer.WriteString("message", "No pending changes to discard.");
        });

    public static CallToolResult FormatDiscard(int id, string title, int notes, int fieldEdits) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("id", id);
            writer.WriteString("title", title);
            writer.WriteBoolean("discarded", true);
            writer.WriteNumber("notesDiscarded", notes);
            writer.WriteNumber("fieldEditsDiscarded", fieldEdits);
        });

    public static CallToolResult FormatFlushSummary(McpFlushSummary summary) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("flushed", summary.Flushed);
            writer.WriteNumber("failed", summary.Failed);
            writer.WriteStartArray("failures");
            foreach (var f in summary.Failures)
            {
                writer.WriteStartObject();
                writer.WriteNumber("workItemId", f.WorkItemId);
                writer.WriteString("reason", f.Reason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });

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

    private static void WriteWorkItemArray(Utf8JsonWriter writer, string name, IEnumerable<WorkItem> items)
    {
        writer.WriteStartArray(name);
        foreach (var item in items)
        {
            writer.WriteStartObject();
            WriteWorkItemCore(writer, item);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
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

    private static void WriteOptionalWorkspace(Utf8JsonWriter writer, string? workspace)
    {
        if (workspace is not null)
            writer.WriteString("workspace", workspace);
        else
            writer.WriteNull("workspace");
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
