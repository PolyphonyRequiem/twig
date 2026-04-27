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

    /// <summary>
    /// Dynamic columns to include in workspace/sprint JSON output.
    /// Set by the command layer after column resolution.
    /// </summary>
    public IReadOnlyList<Domain.ValueObjects.ColumnSpec>? DynamicColumns { get; set; }

    public string FormatStatusSummary(WorkItem item) => string.Empty;

    public string FormatWorkItem(WorkItem item, bool showDirty)
    {
        return FormatWorkItem(item, showDirty, links: null);
    }

    public string FormatWorkItem(WorkItem item, bool showDirty, IReadOnlyList<WorkItemLink>? links,
        WorkItem? parent = null, IReadOnlyList<WorkItem>? children = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        WriteCoreFields(writer, item, showDirty);

        // Relationships — hierarchy + non-hierarchy
        if (parent is not null)
        {
            writer.WritePropertyName("parent");
            writer.WriteStartObject();
            writer.WriteNumber("id", parent.Id);
            writer.WriteString("title", parent.Title);
            writer.WriteString("type", parent.Type.ToString());
            writer.WriteEndObject();
        }

        if (children is { Count: > 0 })
        {
            writer.WriteStartArray("children");
            foreach (var child in children)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", child.Id);
                writer.WriteString("title", child.Title);
                writer.WriteString("type", child.Type.ToString());
                writer.WriteString("state", child.State);
                writer.WriteString("tags", GetTags(child));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        if (links is { Count: > 0 })
        {
            writer.WriteStartArray("links");
            foreach (var link in links)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sourceId", link.SourceId);
                writer.WriteNumber("targetId", link.TargetId);
                writer.WriteString("linkType", link.LinkType);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Formats a batch of work items as a JSON array. Each element uses the same
    /// top-level schema as <see cref="FormatWorkItem(WorkItem, bool)"/> but without
    /// relationship enrichment (parent, children, links).
    /// </summary>
    public string FormatWorkItemBatch(IReadOnlyList<WorkItem> items)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartArray();
        foreach (var item in items)
        {
            writer.WriteStartObject();
            WriteCoreFields(writer, item, showDirty: false);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // activeId is accepted per the IOutputFormatter contract but not serialized —
    // JSON consumers derive active state from the "focus" object.
    public string FormatTree(WorkTree tree, int maxDepth, int? activeId)
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

        // All children (recursive for JSON consumers)
        writer.WriteStartArray("children");
        foreach (var child in tree.Children)
        {
            WriteTreeNodeRecursive(writer, child, tree);
        }
        writer.WriteEndArray();

        writer.WriteNumber("totalChildren", tree.Children.Count);

        // Non-hierarchy links for the focused item
        writer.WriteStartArray("links");
        foreach (var link in tree.FocusedItemLinks)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sourceId", link.SourceId);
            writer.WriteNumber("targetId", link.TargetId);
            writer.WriteString("linkType", link.LinkType);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

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
            WriteWorkItemObject(writer, item, DynamicColumns);
        }
        writer.WriteEndArray();

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in ws.Seeds)
        {
            WriteWorkItemObject(writer, seed, DynamicColumns);
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

        // Mode sections (when available)
        if (ws.Sections is not null)
            WriteSectionsBlock(writer, ws.Sections);

        // Tracked items
        if (ws.TrackedItems.Count > 0)
        {
            writer.WriteStartArray("trackedItems");
            foreach (var t in ws.TrackedItems)
            {
                writer.WriteStartObject();
                writer.WriteNumber("workItemId", t.WorkItemId);
                writer.WriteString("mode", t.Mode.ToString());
                writer.WriteString("trackedAt", t.TrackedAt.ToString("O"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        // Excluded IDs
        if (ws.ExcludedIds.Count > 0)
        {
            writer.WriteStartArray("excludedIds");
            foreach (var id in ws.ExcludedIds)
                writer.WriteNumberValue(id);
            writer.WriteEndArray();
        }

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
                WriteWorkItemObject(writer, item, DynamicColumns);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteNumber("totalSprintItems", ws.SprintItems.Count);

        // Seeds
        writer.WriteStartArray("seeds");
        foreach (var seed in ws.Seeds)
        {
            WriteWorkItemObject(writer, seed, DynamicColumns);
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

    public string FormatSeedView(
        IReadOnlyList<SeedViewGroup> groups,
        int totalWritableFields,
        int staleDays,
        IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        writer.WriteStartArray("groups");
        foreach (var group in groups)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("parent");
            if (group.Parent is not null)
                WriteWorkItemObject(writer, group.Parent);
            else
                writer.WriteNullValue();

            writer.WriteStartArray("seeds");
            foreach (var seed in group.Seeds)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", seed.Id);
                writer.WriteString("title", seed.Title);
                writer.WriteString("type", seed.Type.ToString());
                if (seed.ParentId.HasValue)
                    writer.WriteNumber("parentId", seed.ParentId.Value);
                else
                    writer.WriteNull("parentId");
                writer.WriteString("seedCreatedAt", seed.SeedCreatedAt?.ToString("o"));
                writer.WriteString("age", HumanOutputFormatter.FormatSeedAge(seed.SeedCreatedAt));

                var filled = HumanOutputFormatter.CountNonEmptyFields(seed);
                writer.WriteNumber("filledFields", filled);
                writer.WriteNumber("totalWritableFields", totalWritableFields);

                writer.WriteBoolean("isStale", HumanOutputFormatter.IsStaleSeed(seed, staleDays));

                // Include links for this seed
                writer.WriteStartArray("links");
                if (links is not null && links.TryGetValue(seed.Id, out var seedLinks))
                {
                    foreach (var link in seedLinks)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("sourceId", link.SourceId);
                        writer.WriteNumber("targetId", link.TargetId);
                        writer.WriteString("linkType", link.LinkType);
                        writer.WriteString("annotation", HumanOutputFormatter.FormatLinkAnnotation(seed.Id, link));
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        var totalSeeds = 0;
        foreach (var g in groups)
            totalSeeds += g.Seeds.Count;
        writer.WriteNumber("totalSeeds", totalSeeds);

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSeedLinks(IReadOnlyList<SeedLink> links)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteStartArray("links");
        foreach (var link in links)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sourceId", link.SourceId);
            writer.WriteNumber("targetId", link.TargetId);
            writer.WriteString("linkType", link.LinkType);
            writer.WriteString("createdAt", link.CreatedAt.ToString("o"));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("count", links.Count);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatWorkItemLinks(IReadOnlyList<WorkItemLink> links)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("count", links.Count);
        writer.WriteStartArray("links");
        foreach (var link in links)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sourceId", link.SourceId);
            writer.WriteNumber("targetId", link.TargetId);
            writer.WriteString("linkType", link.LinkType);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteStartArray("results");
        var passCount = 0;
        foreach (var result in results)
        {
            if (result.Passed) passCount++;
            writer.WriteStartObject();
            writer.WriteNumber("seedId", result.SeedId);
            writer.WriteString("title", result.Title);
            writer.WriteBoolean("passed", result.Passed);
            writer.WriteStartArray("failures");
            foreach (var f in result.Failures)
            {
                writer.WriteStartObject();
                writer.WriteString("rule", f.Rule);
                writer.WriteString("message", f.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("passed", passCount);
        writer.WriteNumber("total", results.Count);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSeedReconcileResult(SeedReconcileResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("linksRepaired", result.LinksRepaired);
        writer.WriteNumber("linksRemoved", result.LinksRemoved);
        writer.WriteNumber("parentIdsFixed", result.ParentIdsFixed);
        writer.WriteBoolean("nothingToDo", result.NothingToDo);
        writer.WriteStartArray("warnings");
        foreach (var warning in result.Warnings)
            writer.WriteStringValue(warning);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSeedPublishResult(SeedPublishResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteNumber("oldId", result.OldId);
        writer.WriteNumber("newId", result.NewId);
        writer.WriteString("title", result.Title);
        writer.WriteString("status", result.Status.ToString());
        writer.WriteBoolean("isSuccess", result.IsSuccess);
        if (result.ErrorMessage is not null)
            writer.WriteString("errorMessage", result.ErrorMessage);
        else
            writer.WriteNull("errorMessage");
        writer.WriteStartArray("linkWarnings");
        foreach (var w in result.LinkWarnings)
            writer.WriteStringValue(w);
        writer.WriteEndArray();
        writer.WriteStartArray("validationFailures");
        foreach (var f in result.ValidationFailures)
        {
            writer.WriteStartObject();
            writer.WriteString("rule", f.Rule);
            writer.WriteString("message", f.Message);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSeedPublishBatchResult(SeedPublishBatchResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteStartArray("results");
        foreach (var r in result.Results)
        {
            writer.WriteStartObject();
            writer.WriteNumber("oldId", r.OldId);
            writer.WriteNumber("newId", r.NewId);
            writer.WriteString("title", r.Title);
            writer.WriteString("status", r.Status.ToString());
            writer.WriteBoolean("isSuccess", r.IsSuccess);
            if (r.ErrorMessage is not null)
                writer.WriteString("errorMessage", r.ErrorMessage);
            else
                writer.WriteNull("errorMessage");
            writer.WriteStartArray("linkWarnings");
            foreach (var w in r.LinkWarnings)
                writer.WriteStringValue(w);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("cycleErrors");
        foreach (var err in result.CycleErrors)
            writer.WriteStringValue(err);
        writer.WriteEndArray();
        writer.WriteNumber("createdCount", result.CreatedCount);
        writer.WriteNumber("skippedCount", result.SkippedCount);
        writer.WriteBoolean("hasErrors", result.HasErrors);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a WorkItem as a JSON object for nested contexts (tree, workspace).
    /// Deliberately omits areaPath and iterationPath to keep nested payloads lean —
    /// consumers who need path data should use FormatWorkItem for standalone items.
    /// When <paramref name="dynamicColumns"/> is provided, includes those field values.
    /// </summary>
    private static void WriteCoreFields(Utf8JsonWriter writer, WorkItem item, bool showDirty)
    {
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
        writer.WriteString("tags", GetTags(item));
        WriteFieldsBlock(writer, item);
    }

    internal static string GetTags(WorkItem item)
    {
        item.Fields.TryGetValue("System.Tags", out var tags);
        return tags ?? "";
    }

    /// <summary>
    /// Writes all populated fields from <see cref="WorkItem.Fields"/> as a <c>"fields"</c> object.
    /// Skips <c>System.Tags</c> (already promoted to a top-level property).
    /// </summary>
    private static void WriteFieldsBlock(Utf8JsonWriter writer, WorkItem item)
    {
        if (item.Fields.Count == 0) return;

        writer.WriteStartObject("fields");
        foreach (var (refName, value) in item.Fields)
        {
            if (string.IsNullOrEmpty(value)) continue;
            // System.Tags is already a top-level property
            if (string.Equals(refName, "System.Tags", StringComparison.OrdinalIgnoreCase)) continue;
            writer.WriteString(refName, value);
        }
        writer.WriteEndObject();
    }

    internal static void WriteSectionsBlock(Utf8JsonWriter writer, WorkspaceSections sections)
    {
        writer.WriteStartArray("sections");
        foreach (var section in sections.Sections)
        {
            writer.WriteStartObject();
            writer.WriteString("modeName", section.ModeName);
            writer.WriteNumber("itemCount", section.Items.Count);
            writer.WriteStartArray("itemIds");
            foreach (var item in section.Items)
                writer.WriteNumberValue(item.Id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("excludedItemIds");
        foreach (var id in sections.ExcludedItemIds)
            writer.WriteNumberValue(id);
        writer.WriteEndArray();
    }

    private static void WriteWorkItemObject(Utf8JsonWriter writer, WorkItem item, IReadOnlyList<Domain.ValueObjects.ColumnSpec>? dynamicColumns = null)
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
        writer.WriteString("tags", GetTags(item));

        if (dynamicColumns is { Count: > 0 })
        {
            // Dynamic columns mode: write only the requested columns (workspace/sprint view)
            writer.WriteStartObject("fields");
            foreach (var col in dynamicColumns)
            {
                item.Fields.TryGetValue(col.ReferenceName, out var rawValue);
                var formatted = FormatterHelpers.FormatFieldValueForJson(rawValue, col.DataType);
                writer.WriteString(col.ReferenceName, formatted);
            }
            writer.WriteEndObject();
        }
        else
        {
            WriteFieldsBlock(writer, item);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a work item object with recursive children from <see cref="WorkTree.DescendantsByParentId"/>.
    /// </summary>
    private static void WriteTreeNodeRecursive(Utf8JsonWriter writer, WorkItem item, WorkTree tree)
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
        writer.WriteString("tags", GetTags(item));
        WriteFieldsBlock(writer, item);

        var descendants = tree.GetDescendants(item.Id);
        writer.WriteStartArray("children");
        foreach (var child in descendants)
        {
            WriteTreeNodeRecursive(writer, child, tree);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    public string FormatQueryResults(QueryResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteString("query", result.Query);
        writer.WriteNumber("count", result.Items.Count);
        writer.WriteBoolean("truncated", result.IsTruncated);

        writer.WriteStartArray("items");
        foreach (var item in result.Items)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", item.Id);
            writer.WriteString("type", item.Type.ToString());
            writer.WriteString("title", item.Title);
            writer.WriteString("state", item.State);
            writer.WriteString("assignedTo", item.AssignedTo);
            writer.WriteString("areaPath", item.AreaPath.ToString());
            writer.WriteString("iterationPath", item.IterationPath.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatAreaView(AreaView areaView)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        // Filters
        writer.WriteStartArray("filters");
        foreach (var filter in areaView.Filters)
        {
            writer.WriteStartObject();
            writer.WriteString("path", filter.Path);
            writer.WriteBoolean("includeChildren", filter.IncludeChildren);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteNumber("matchCount", areaView.MatchCount);

        // Area items
        writer.WriteStartArray("items");
        foreach (var item in areaView.AreaItems)
        {
            WriteWorkItemObject(writer, item);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
