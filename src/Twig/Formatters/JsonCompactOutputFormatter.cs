using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;

namespace Twig.Formatters;

/// <summary>
/// Compact JSON formatter emitting slim work item schemas (id, title, type, state).
/// Delegates utility methods (error, success, hint, seed operations) to
/// <see cref="JsonOutputFormatter"/> to avoid duplication.
/// </summary>
public sealed class JsonCompactOutputFormatter(JsonOutputFormatter full) : IOutputFormatter
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
        if (showDirty && item.IsDirty)
            writer.WriteBoolean("isDirty", true);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatTree(WorkTree tree, int maxChildren, int? activeId)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();

        writer.WritePropertyName("focus");
        WriteCompactItem(writer, tree.FocusedItem);

        writer.WriteStartArray("parentChain");
        foreach (var parent in tree.ParentChain)
            WriteCompactItem(writer, parent);
        writer.WriteEndArray();

        writer.WriteStartArray("children");
        foreach (var child in tree.Children)
            WriteCompactItem(writer, child);
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

        writer.WritePropertyName("context");
        if (ws.ContextItem is not null)
            WriteCompactItem(writer, ws.ContextItem);
        else
            writer.WriteNullValue();

        writer.WriteStartArray("sprintItems");
        foreach (var item in ws.SprintItems)
            WriteCompactItem(writer, item);
        writer.WriteEndArray();

        writer.WriteStartArray("seeds");
        foreach (var seed in ws.Seeds)
            WriteCompactItem(writer, seed);
        writer.WriteEndArray();

        writer.WriteNumber("dirtyCount", ws.GetDirtyItems().Count);
        writer.WriteEndObject();

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string FormatSprintView(Workspace ws, int staleDays)
        => FormatWorkspace(ws, staleDays);

    // ── Delegate to full formatter for utility/structural methods ────

    public string FormatFieldChange(FieldChange change) => full.FormatFieldChange(change);
    public string FormatError(string message) => full.FormatError(message);
    public string FormatSuccess(string message) => full.FormatSuccess(message);
    public string FormatDisambiguation(IReadOnlyList<(int Id, string Title)> matches) => full.FormatDisambiguation(matches);
    public string FormatHint(string hint) => full.FormatHint(hint);
    public string FormatInfo(string message) => full.FormatInfo(message);
    public string FormatBranchInfo(string branchName) => full.FormatBranchInfo(branchName);
    public string FormatPrStatus(int prId, string title, string status) => full.FormatPrStatus(prId, title, status);
    public string FormatAnnotatedLogEntry(string hash, string message, string? workItemType, string? workItemState, int? workItemId)
        => full.FormatAnnotatedLogEntry(hash, message, workItemType, workItemState, workItemId);
    public string FormatSeedView(IReadOnlyList<SeedViewGroup> groups, int totalWritableFields, int staleDays, IReadOnlyDictionary<int, IReadOnlyList<SeedLink>>? links = null)
        => full.FormatSeedView(groups, totalWritableFields, staleDays, links);
    public string FormatSeedLinks(IReadOnlyList<SeedLink> links) => full.FormatSeedLinks(links);
    public string FormatSeedValidation(IReadOnlyList<SeedValidationResult> results) => full.FormatSeedValidation(results);
    public string FormatSeedReconcileResult(SeedReconcileResult result) => full.FormatSeedReconcileResult(result);
    public string FormatSeedPublishResult(SeedPublishResult result) => full.FormatSeedPublishResult(result);
    public string FormatSeedPublishBatchResult(SeedPublishBatchResult result) => full.FormatSeedPublishBatchResult(result);

    // ── Private helpers ─────────────────────────────────────────────

    private static void WriteCompactItem(Utf8JsonWriter writer, WorkItem item)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", item.Id);
        writer.WriteString("title", item.Title);
        writer.WriteString("type", item.Type.ToString());
        writer.WriteString("state", item.State);
        writer.WriteEndObject();
    }
}
