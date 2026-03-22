using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.Formatters;

/// <summary>
/// JSON formatter producing stable-schema output suitable for piping and automation.
/// Uses manual JSON writing for AOT compatibility (no reflection).
/// </summary>
public sealed class JsonOutputFormatter : IOutputFormatter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    public string FormatStatusSummary(WorkItem item) => string.Empty;

    public string FormatWorkItem(WorkItem item, bool showDirty)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("id", item.Id);
        writer.WriteString("title", item.Title);
        writer.WriteString("type", item.Type.ToString());
        writer.WriteString("state", item.State);
        writer.WriteString("assignedTo", item.AssignedTo);
        writer.WriteString("areaPath", item.AreaPath.ToString());
        writer.WriteString("iterationPath", item.IterationPath.ToString());
        writer.WriteBoolean("isDirty", showDirty && item.IsDirty);
        writer.WriteBoolean("isSeed", item.IsSeed);
        if (item.ParentId.HasValue)
            writer.WriteNumber("parentId", item.ParentId.Value);
        else
            writer.WriteNull("parentId");
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // activeId is accepted per the IOutputFormatter contract but not serialized —
    // JSON consumers derive active state from the "focus" object.
    public string FormatTree(WorkTree tree, int maxChildren, int? activeId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        // Focus
        writer.WritePropertyName("focus");
        WriteWorkItemObject(writer, tree.FocusedItem);

        // Full parent chain (no truncation for JSON consumers)
        writer.WriteStartArray("parentChain");
        foreach (var parent in tree.ParentChain)
        {
            WriteWorkItemObject(writer, parent);
        }
        writer.WriteEndArray();

        // All children (no truncation for JSON consumers)
        writer.WriteStartArray("children");
        foreach (var child in tree.Children)
        {
            WriteWorkItemObject(writer, child);
        }
        writer.WriteEndArray();

        writer.WriteNumber("totalChildren", tree.Children.Count);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatWorkspace(Workspace ws, int staleDays)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        // Context
        writer.WritePropertyName("context");
        if (ws.ContextItem is not null)
            WriteWorkItemObject(writer, ws.ContextItem);
        else
            writer.WriteNullValue();

        // Sprint items
        writer.WriteStartArray("sprintItems");
        foreach (var item in ws.SprintItems)
        {
            WriteWorkItemObject(writer, item);
        }
        writer.WriteEndArray();

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in ws.Seeds)
        {
            WriteWorkItemObject(writer, seed);
        }
        writer.WriteEndArray();

        // Stale seeds
        var staleSeeds = ws.GetStaleSeeds(staleDays);
        writer.WriteStartArray("staleSeeds");
        foreach (var s in staleSeeds)
        {
            writer.WriteNumberValue(s.Id);
        }
        writer.WriteEndArray();

        // Dirty items
        var dirtyItems = ws.GetDirtyItems();
        writer.WriteNumber("dirtyCount", dirtyItems.Count);

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSprintView(Workspace ws, int staleDays)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        // Context
        writer.WritePropertyName("context");
        if (ws.ContextItem is not null)
            WriteWorkItemObject(writer, ws.ContextItem);
        else
            writer.WriteNullValue();

        // Sprint items grouped by assignee
        writer.WriteStartObject("sprintByAssignee");
        var grouped = new Dictionary<string, List<WorkItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ws.SprintItems)
        {
            var assignee = item.AssignedTo ?? "";
            if (!grouped.TryGetValue(assignee, out var list))
            {
                list = new List<WorkItem>();
                grouped[assignee] = list;
            }
            list.Add(item);
        }
        foreach (var kvp in grouped.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteStartArray(kvp.Key);
            foreach (var item in kvp.Value)
            {
                WriteWorkItemObject(writer, item);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteNumber("totalSprintItems", ws.SprintItems.Count);

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in ws.Seeds)
        {
            WriteWorkItemObject(writer, seed);
        }
        writer.WriteEndArray();

        // Dirty items
        var dirtyItems = ws.GetDirtyItems();
        writer.WriteNumber("dirtyCount", dirtyItems.Count);

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatFieldChange(FieldChange change)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("field", change.FieldName);
        writer.WriteString("oldValue", change.OldValue);
        writer.WriteString("newValue", change.NewValue);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatError(string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("error", message);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSuccess(string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("message", message);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteStartArray("matches");
        foreach (var (id, title) in matches)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("title", title);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatHint(string hint)
    {
        return "";
    }

    public string FormatInfo(string message)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("info", message);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatBranchInfo(string branchName)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("branch", branchName);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatPrStatus(int prId, string title, string status)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("prId", prId);
        writer.WriteString("title", title);
        writer.WriteString("status", status);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("hash", hash);
        writer.WriteString("message", message);
        if (workItemId.HasValue)
        {
            writer.WriteNumber("workItemId", workItemId.Value);
            if (workItemType is not null) writer.WriteString("workItemType", workItemType);
            if (workItemState is not null) writer.WriteString("workItemState", workItemState);
        }
        else
        {
            writer.WriteNull("workItemId");
        }
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a WorkItem as a JSON object for nested contexts (tree, workspace).
    /// Deliberately omits areaPath and iterationPath to keep nested payloads lean —
    /// consumers who need path data should use FormatWorkItem for standalone items.
    /// </summary>
    private static void WriteWorkItemObject(Utf8JsonWriter writer, WorkItem item)
    {
        writer.WriteStartObject();
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
        writer.WriteEndObject();
    }
}
