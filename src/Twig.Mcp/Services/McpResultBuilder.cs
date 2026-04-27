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
            WriteWorkItemWithPaths(writer, item);
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
                WriteWorkItemWithPaths(writer, snapshot.Item);
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

            // Children (recursive for MCP consumers)
            writer.WriteStartArray("children");
            foreach (var child in tree.Children)
            {
                WriteTreeNodeRecursive(writer, child, tree);
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

    public static CallToolResult FormatWorkspace(
        Workspace workspace, int staleDays, string? workspaceKey = null,
        IReadOnlyList<ExcludedItem>? excludedItems = null) =>
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

            // Tracked items
            writer.WriteStartArray("trackedItems");
            foreach (var t in workspace.TrackedItems)
            {
                writer.WriteStartObject();
                writer.WriteNumber("workItemId", t.WorkItemId);
                writer.WriteString("mode", t.Mode.ToString());
                writer.WriteString("trackedAt", t.TrackedAt.ToString("o"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Excluded items
            var excluded = excludedItems ?? Array.Empty<ExcludedItem>();
            writer.WriteStartArray("excludedItems");
            foreach (var e in excluded)
            {
                writer.WriteStartObject();
                writer.WriteNumber("workItemId", e.WorkItemId);
                writer.WriteString("reason", e.Reason);
                writer.WriteString("excludedAt", e.ExcludedAt.ToString("o"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

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

    public static CallToolResult FormatWorkItem(WorkItem item, string? workspace = null) =>
        BuildJson(writer =>
        {
            WriteWorkItemWithPaths(writer, item);

            if (item.Fields.Count > 0)
            {
                writer.WriteStartObject("fields");
                foreach (var (key, value) in item.Fields)
                {
                    if (value is not null)
                        writer.WriteString(key, value);
                    else
                        writer.WriteNull(key);
                }
                writer.WriteEndObject();
            }

            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatQueryResults(
        IReadOnlyList<WorkItem> items, bool isTruncated, string queryDescription, string? workspace = null) =>
        BuildJson(writer =>
        {
            WriteWorkItemArray(writer, "items", items);
            writer.WriteNumber("totalCount", items.Count);
            writer.WriteBoolean("isTruncated", isTruncated);
            writer.WriteString("queryDescription", queryDescription);
            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatChildren(int parentId, IReadOnlyList<WorkItem> children, string? workspace = null) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("parentId", parentId);
            WriteWorkItemArray(writer, "children", children);
            writer.WriteNumber("count", children.Count);
            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatParent(WorkItem child, WorkItem? parent, string? workspace = null) =>
        BuildJson(writer =>
        {
            writer.WritePropertyName("child");
            writer.WriteStartObject();
            WriteWorkItemCore(writer, child);
            writer.WriteEndObject();

            if (parent is not null)
            {
                writer.WritePropertyName("parent");
                writer.WriteStartObject();
                WriteWorkItemWithPaths(writer, parent);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("parent");
            }

            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatSprint(
        IterationPath iterationPath, IReadOnlyList<WorkItem>? items, string? workspace = null) =>
        BuildJson(writer =>
        {
            writer.WriteString("iterationPath", iterationPath.ToString());

            if (items is not null)
            {
                WriteWorkItemArray(writer, "items", items);
                writer.WriteNumber("count", items.Count);
            }
            else
            {
                writer.WriteNull("items");
            }

            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatCreated(WorkItem item, string url, string? workspace = null) =>
        FormatWithAction("created", item, url, workspace);

    public static CallToolResult FormatFoundExisting(WorkItem item, string url, string? workspace = null) =>
        FormatWithAction("found_existing", item, url, workspace);

    private static CallToolResult FormatWithAction(string action, WorkItem item, string url, string? workspace) =>
        BuildJson(writer =>
        {
            writer.WriteString("action", action);
            WriteWorkItemWithPaths(writer, item);
            writer.WriteString("url", url);
            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatBatchResult(Batch.BatchResult batch) =>
        BuildJson(writer =>
        {
            var succeeded = 0;
            var failed = 0;
            var skipped = 0;

            writer.WriteStartArray("steps");
            foreach (var step in batch.Steps)
            {
                switch (step.Status)
                {
                    case Batch.StepStatus.Succeeded: succeeded++; break;
                    case Batch.StepStatus.Failed: failed++; break;
                    case Batch.StepStatus.Skipped: skipped++; break;
                }

                writer.WriteStartObject();
                writer.WriteNumber("index", step.StepIndex);
                writer.WriteString("tool", step.ToolName);
                writer.WriteString("status", step.Status switch
                {
                    Batch.StepStatus.Succeeded => "succeeded",
                    Batch.StepStatus.Failed => "failed",
                    Batch.StepStatus.Skipped => "skipped",
                    _ => "unknown"
                });

                // Embed the tool's output JSON as a nested object when available.
                if (step.OutputJson is not null)
                {
                    writer.WritePropertyName("output");
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(step.OutputJson);
                        doc.RootElement.WriteTo(writer);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // If the output isn't valid JSON, emit it as a string.
                        writer.WriteStringValue(step.OutputJson);
                    }
                }
                else
                {
                    writer.WriteNull("output");
                }

                if (step.Error is not null)
                    writer.WriteString("error", step.Error);

                writer.WriteNumber("elapsedMs", step.ElapsedMs);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartObject("summary");
            writer.WriteNumber("total", batch.Steps.Count);
            writer.WriteNumber("succeeded", succeeded);
            writer.WriteNumber("failed", failed);
            writer.WriteNumber("skipped", skipped);
            writer.WriteEndObject();

            writer.WriteNumber("totalElapsedMs", batch.TotalElapsedMs);
            writer.WriteBoolean("timedOut", batch.TimedOut);
        });

    public static CallToolResult FormatLinked(int sourceId, int targetId, string linkType, string? warning = null) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("sourceId", sourceId);
            writer.WriteNumber("targetId", targetId);
            writer.WriteString("linkType", linkType);
            writer.WriteBoolean("linked", true);
            if (warning is not null)
                writer.WriteString("warning", warning);
        });

    public static CallToolResult FormatArtifactLinked(int workItemId, string url, bool alreadyLinked) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("workItemId", workItemId);
            writer.WriteString("url", url);
            writer.WriteBoolean("alreadyLinked", alreadyLinked);
            writer.WriteString("message", alreadyLinked
                ? $"Link already exists on #{workItemId}."
                : $"Artifact link added to #{workItemId}.");
        });

    public static CallToolResult FormatBranchLinked(BranchLinkResult result) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("workItemId", result.WorkItemId);
            writer.WriteString("branchName", result.BranchName);
            writer.WriteString("artifactUri", result.ArtifactUri);
            writer.WriteBoolean("alreadyLinked", result.Status == BranchLinkStatus.AlreadyLinked);
            writer.WriteString("message", result.Status == BranchLinkStatus.AlreadyLinked
                ? $"Branch '{result.BranchName}' already linked to #{result.WorkItemId}."
                : $"Branch '{result.BranchName}' linked to #{result.WorkItemId}.");
        });

    public static CallToolResult FormatVerification(DescendantVerificationResult result, string? workspace) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("rootId", result.RootId);
            writer.WriteBoolean("verified", result.Verified);
            writer.WriteNumber("totalChecked", result.TotalChecked);
            writer.WriteNumber("incompleteCount", result.Incomplete.Count);

            writer.WriteStartArray("incomplete");
            foreach (var item in result.Incomplete)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", item.Id);
                writer.WriteString("title", item.Title);
                writer.WriteString("type", item.Type);
                writer.WriteString("state", item.State);
                if (item.ParentId.HasValue)
                    writer.WriteNumber("parentId", item.ParentId.Value);
                else
                    writer.WriteNull("parentId");
                writer.WriteNumber("depth", item.Depth);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            WriteOptionalWorkspace(writer, workspace);
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

    private static void WriteWorkItemWithPaths(Utf8JsonWriter writer, WorkItem item)
    {
        WriteWorkItemCore(writer, item);
        writer.WriteString("areaPath", item.AreaPath.ToString());
        writer.WriteString("iterationPath", item.IterationPath.ToString());
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
        item.Fields.TryGetValue("System.Tags", out var tags);
        writer.WriteString("tags", tags ?? "");

        if (item.Fields.Count > 0)
        {
            writer.WriteStartObject("fields");
            foreach (var (refName, value) in item.Fields)
            {
                if (string.IsNullOrEmpty(value)) continue;
                if (string.Equals(refName, "System.Tags", StringComparison.OrdinalIgnoreCase)) continue;
                writer.WriteString(refName, value);
            }
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Always writes the "workspace" key: string value when workspace is known,
    /// JSON null when absent. Callers that check key presence will always find it.
    /// </summary>
    private static void WriteOptionalWorkspace(Utf8JsonWriter writer, string? workspace)
    {
        if (workspace is not null)
            writer.WriteString("workspace", workspace);
        else
            writer.WriteNull("workspace");
    }

    /// <summary>
    /// Writes a work item object with recursive children from <see cref="WorkTree.DescendantsByParentId"/>.
    /// </summary>
    private static void WriteTreeNodeRecursive(Utf8JsonWriter writer, WorkItem item, WorkTree tree)
    {
        writer.WriteStartObject();
        WriteWorkItemCore(writer, item);

        var descendants = tree.GetDescendants(item.Id);
        writer.WriteStartArray("children");
        foreach (var child in descendants)
        {
            WriteTreeNodeRecursive(writer, child, tree);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
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
