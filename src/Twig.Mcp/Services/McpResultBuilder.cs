using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
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

    public static CallToolResult FormatStatus(StatusResult status, string? workspace = null) =>
        BuildJson(writer =>
        {
            switch (status)
            {
                case StatusNoContext:
                    writer.WriteBoolean("hasContext", false);
                    writer.WriteNull("item");
                    writer.WriteStartArray("pendingChanges");
                    writer.WriteEndArray();
                    WriteWorkItemArray(writer, "seeds", []);
                    break;

                case StatusUnreachable u:
                    writer.WriteBoolean("hasContext", true);
                    writer.WriteNull("item");
                    writer.WriteStartArray("pendingChanges");
                    writer.WriteEndArray();
                    WriteWorkItemArray(writer, "seeds", []);
                    writer.WriteNumber("unreachableId", u.UnreachableId);
                    writer.WriteString("unreachableReason", u.Reason);
                    break;

                case StatusSuccess s:
                    writer.WriteBoolean("hasContext", true);
                    writer.WritePropertyName("item");
                    writer.WriteStartObject();
                    WriteWorkItemWithPaths(writer, s.Item);
                    writer.WriteEndObject();
                    writer.WriteStartArray("pendingChanges");
                    foreach (var change in s.PendingChanges)
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
                    WriteWorkItemArray(writer, "seeds", s.Seeds);
                    break;

                default:
                    throw new System.Diagnostics.UnreachableException(
                        $"Unhandled StatusResult: {status.GetType().Name}");
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

    public static CallToolResult FormatWorkspaceTree(
        IReadOnlyList<(WorkTree Tree, int TotalChildren)> roots,
        Workspace workspace, string? workspaceKey = null,
        IReadOnlyList<ExcludedItem>? excludedItems = null) =>
        BuildJson(writer =>
        {
            writer.WriteString("workspace", workspaceKey ?? "");
            writer.WriteString("mode", "tree");

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

            // Tree roots
            var totalItems = 0;
            writer.WriteStartArray("roots");
            foreach (var (tree, totalChildren) in roots)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("focus");
                writer.WriteStartObject();
                WriteWorkItemCore(writer, tree.FocusedItem);
                writer.WriteEndObject();

                writer.WriteStartArray("children");
                foreach (var child in tree.Children)
                {
                    WriteTreeNodeRecursive(writer, child, tree);
                }
                writer.WriteEndArray();

                writer.WriteNumber("totalChildren", totalChildren);
                writer.WriteEndObject();

                totalItems += 1 + totalChildren;
            }
            writer.WriteEndArray();

            writer.WriteNumber("totalItems", totalItems);

            // Seeds
            WriteWorkItemArray(writer, "seeds", workspace.Seeds);

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

    public static CallToolResult FormatPatch(
        WorkItem updated,
        IReadOnlyDictionary<string, (string? OldValue, string? NewValue)> fieldChanges,
        string? workspace = null) =>
        BuildJson(writer =>
        {
            WriteWorkItemCore(writer, updated);

            writer.WriteStartObject("updatedFields");
            foreach (var (field, change) in fieldChanges)
            {
                writer.WriteStartObject(field);
                if (change.OldValue is not null)
                    writer.WriteString("old", change.OldValue);
                else
                    writer.WriteNull("old");
                if (change.NewValue is not null)
                    writer.WriteString("new", change.NewValue);
                else
                    writer.WriteNull("new");
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            writer.WriteNumber("fieldCount", fieldChanges.Count);
            WriteOptionalWorkspace(writer, workspace);
        });

    public static CallToolResult FormatNoteAdded(int itemId, string title, bool isPending) =>
        BuildJson(writer =>
        {
            writer.WriteNumber("id", itemId);
            writer.WriteString("title", title);
            writer.WriteBoolean("noteAdded", true);
            writer.WriteBoolean("isPending", isPending);
        });

    public static CallToolResult FormatWorkItem(WorkItem item, string? workspace = null) =>
        FormatWorkItem(item, pendingChanges: null, workspace: workspace);

    public static CallToolResult FormatWorkItem(
        WorkItem item,
        IReadOnlyList<PendingChangeRecord>? pendingChanges,
        string? workspace = null) =>
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

            WritePendingChanges(writer, pendingChanges);
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
            writer.WriteStartArray("steps");
            foreach (var step in batch.Steps)
            {
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
            writer.WriteNumber("total", batch.Summary.Total);
            writer.WriteNumber("succeeded", batch.Summary.Succeeded);
            writer.WriteNumber("failed", batch.Summary.Failed);
            writer.WriteNumber("skipped", batch.Summary.Skipped);
            writer.WriteEndObject();

            writer.WriteNumber("totalElapsedMs", batch.TotalElapsedMs);
            writer.WriteBoolean("timedOut", batch.TimedOut);
        });

    public static CallToolResult FormatLinkBatch(IReadOnlyList<LinkBatchItemResult> results) =>
        BuildJson(writer =>
        {
            var succeeded = 0;
            var failed = 0;
            foreach (var r in results)
            {
                if (r.Success) succeeded++;
                else failed++;
            }

            writer.WriteNumber("totalOperations", results.Count);
            writer.WriteNumber("succeeded", succeeded);
            writer.WriteNumber("failed", failed);

            writer.WriteStartArray("operations");
            foreach (var r in results)
            {
                writer.WriteStartObject();
                writer.WriteNumber("itemId", r.ItemId);
                writer.WriteString("op", r.Op);
                writer.WriteBoolean("success", r.Success);
                if (r.Error is not null)
                    writer.WriteString("error", r.Error);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
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

    public static CallToolResult FormatBranchLinked(BranchLinkResult result) => result switch
    {
        AlreadyLinked al => BuildJson(writer =>
        {
            writer.WriteNumber("workItemId", al.WorkItemId);
            writer.WriteString("branchName", al.BranchName);
            writer.WriteString("artifactUri", al.ArtifactUri);
            writer.WriteBoolean("alreadyLinked", true);
            writer.WriteString("message", $"Branch '{al.BranchName}' already linked to #{al.WorkItemId}.");
        }),
        Linked l => BuildJson(writer =>
        {
            writer.WriteNumber("workItemId", l.WorkItemId);
            writer.WriteString("branchName", l.BranchName);
            writer.WriteString("artifactUri", l.ArtifactUri);
            writer.WriteBoolean("alreadyLinked", false);
            writer.WriteString("message", $"Branch '{l.BranchName}' linked to #{l.WorkItemId}.");
        }),
        GitContextUnavailable g => BuildErrorJson(writer =>
        {
            writer.WriteString("status", "git-context-unavailable");
            writer.WriteNumber("workItemId", g.WorkItemId);
            writer.WriteString("branchName", g.BranchName);
            writer.WriteString("errorMessage", g.ErrorMessage);
        }),
        LinkFailed f => BuildErrorJson(writer =>
        {
            writer.WriteString("status", "failed");
            writer.WriteNumber("workItemId", f.WorkItemId);
            writer.WriteString("branchName", f.BranchName);
            writer.WriteString("artifactUri", f.ArtifactUri);
            writer.WriteString("errorMessage", f.ErrorMessage);
        }),
        _ => throw new System.Diagnostics.UnreachableException(
            $"Unhandled BranchLinkResult: {result.GetType().Name}"),
    };

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

    public static CallToolResult FormatDeleteConfirmation(WorkItem item) =>
        BuildJson(writer =>
        {
            writer.WriteBoolean("requiresConfirmation", true);
            writer.WriteNumber("id", item.Id);
            writer.WriteString("title", item.Title);
            writer.WriteString("type", item.Type.ToString());
            writer.WriteString("state", item.State);
            writer.WriteString("warning",
                "This action is PERMANENT and cannot be undone. " +
                "Consider 'twig_state Closed' instead — it preserves history and is reversible. " +
                "Re-invoke with confirmed: true to proceed.");
        });

    public static CallToolResult FormatDeleted(int id, string title) =>
        BuildJson(writer =>
        {
            writer.WriteBoolean("deleted", true);
            writer.WriteNumber("id", id);
            writer.WriteString("title", title);
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

    public static CallToolResult FormatSyncSummary(McpFlushSummary? flushSummary, bool pullOnly) =>
        BuildJson(writer =>
        {
            writer.WriteBoolean("pullOnly", pullOnly);
            writer.WriteNumber("flushed", flushSummary?.Flushed ?? 0);
            writer.WriteNumber("failed", flushSummary?.Failed ?? 0);
            writer.WriteStartArray("failures");
            if (flushSummary is not null)
            {
                foreach (var f in flushSummary.Failures)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("workItemId", f.WorkItemId);
                    writer.WriteString("reason", f.Reason);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
        });

    public static CallToolResult FormatProcessList(IReadOnlyList<ProcessTypeRecord> types) =>
        BuildJson(writer =>
        {
            writer.WriteStartArray("types");
            foreach (var type in types)
            {
                writer.WriteStartObject();
                writer.WriteString("typeName", type.TypeName);
                writer.WriteNumber("stateCount", type.States.Count);
                writer.WriteNumber("childTypeCount", type.ValidChildTypes.Count);
                if (type.ColorHex is not null)
                    writer.WriteString("color", type.ColorHex);
                else
                    writer.WriteNull("color");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("totalTypes", types.Count);
        });

    public static CallToolResult FormatProcessType(
        ProcessTypeRecord type, IReadOnlyList<FieldDefinition> fields) =>
        BuildJson(writer =>
        {
            writer.WriteString("typeName", type.TypeName);

            if (type.ColorHex is not null)
                writer.WriteString("color", type.ColorHex);
            else
                writer.WriteNull("color");

            // States with category and color
            writer.WriteStartArray("states");
            foreach (var state in type.States)
            {
                writer.WriteStartObject();
                writer.WriteString("name", state.Name);
                writer.WriteString("category", state.Category.ToString());
                if (state.Color is not null)
                    writer.WriteString("color", state.Color);
                else
                    writer.WriteNull("color");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Fields with reference name and data type
            writer.WriteStartArray("fields");
            foreach (var field in fields)
            {
                writer.WriteStartObject();
                writer.WriteString("referenceName", field.ReferenceName);
                writer.WriteString("displayName", field.DisplayName);
                writer.WriteString("dataType", field.DataType);
                writer.WriteBoolean("isReadOnly", field.IsReadOnly);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // Transitions: enumerate all valid (from, to) pairs from states
            writer.WriteStartArray("transitions");
            for (var i = 0; i < type.States.Count; i++)
            {
                for (var j = 0; j < type.States.Count; j++)
                {
                    if (i == j) continue;
                    var from = type.States[i];
                    var to = type.States[j];
                    writer.WriteStartObject();
                    writer.WriteString("from", from.Name);
                    writer.WriteString("to", to.Name);
                    writer.WriteString("kind", to.Category == StateCategory.Removed
                        ? "Cut" : "Forward");
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();

            // Valid child types
            writer.WriteStartArray("validChildTypes");
            foreach (var childType in type.ValidChildTypes)
            {
                writer.WriteStringValue(childType);
            }
            writer.WriteEndArray();

            writer.WriteNumber("stateCount", type.States.Count);
            writer.WriteNumber("fieldCount", fields.Count);
        });

    public static CallToolResult FormatDiscarded(int id, int notesDiscarded, int fieldEditsDiscarded) =>
        BuildJson(writer =>
        {
            writer.WriteBoolean("discarded", true);
            writer.WriteNumber("id", id);
            writer.WriteNumber("notesDiscarded", notesDiscarded);
            writer.WriteNumber("fieldEditsDiscarded", fieldEditsDiscarded);
        });

    public static CallToolResult FormatDiscardedNone(int id, string title) =>
        BuildJson(writer =>
        {
            writer.WriteBoolean("discarded", false);
            writer.WriteNumber("id", id);
            writer.WriteString("message", $"No pending changes for #{id} '{title}'.");
        });

    /// <summary>
    /// Appends a <c>"hints"</c> array to an existing successful <see cref="CallToolResult"/>.
    /// When <paramref name="hints"/> is empty, writes an empty array <c>[]</c>.
    /// Error results are returned unchanged.
    /// </summary>
    public static CallToolResult WithHints(CallToolResult result, IReadOnlyList<string> hints)
    {
        if (result.IsError == true || result.Content.Count == 0 || result.Content[0] is not TextContentBlock text)
            return result;

        using var doc = JsonDocument.Parse(text.Text);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
            prop.WriteTo(writer);
        writer.WriteStartArray("hints");
        foreach (var hint in hints)
            writer.WriteStringValue(hint);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return ToResult(Encoding.UTF8.GetString(stream.ToArray()));
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

    private static CallToolResult BuildErrorJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        write(writer);
        writer.WriteEndObject();
        writer.Flush();
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(stream.ToArray()) }],
            IsError = true,
        };
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
    /// Writes a "pendingChanges" array when pending changes are provided.
    /// Omits the property entirely when <paramref name="changes"/> is null (no active context match).
    /// </summary>
    private static void WritePendingChanges(Utf8JsonWriter writer, IReadOnlyList<PendingChangeRecord>? changes)
    {
        if (changes is null) return;

        writer.WriteStartArray("pendingChanges");
        foreach (var change in changes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("workItemId", change.WorkItemId);
            writer.WriteString("changeType", change.ChangeType);
            writer.WriteString("fieldName", change.FieldName ?? "");
            writer.WriteString("oldValue", change.OldValue ?? "");
            writer.WriteString("newValue", change.NewValue ?? "");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
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

public sealed record LinkBatchItemResult(int ItemId, string Op, bool Success, string? Error = null);