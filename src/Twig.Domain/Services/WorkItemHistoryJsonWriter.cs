using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services;

/// <summary>
/// The single AOT-safe JSON writer for work-item history (twig#241). CLI and MCP share this
/// projection so behavior learned on one surface transfers to the other — the emitted document
/// shape is identical on both.
/// </summary>
/// <remarks>
/// Hand-written with <see cref="Utf8JsonWriter"/> rather than a serializer: the document has a
/// dynamic field-name keyspace (ADO reference names) and must remain reflection-free under AOT.
/// </remarks>
public static class WorkItemHistoryJsonWriter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Serializes a history document to indented JSON.</summary>
    public static string Write(WorkItemHistory history)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            WriteTo(writer, history);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the history document into an existing writer. Used by the MCP envelope so the
    /// <c>data</c> block carries byte-identical content to the CLI's JSON output.
    /// </summary>
    public static void WriteTo(Utf8JsonWriter writer, WorkItemHistory history)
    {
        writer.WriteStartObject();
        writer.WriteNumber("workItemId", history.WorkItemId);
        writer.WriteBoolean("complete", history.Complete);
        writer.WriteNumber("eventCount", history.Events.Count);

        writer.WriteStartArray("events");
        foreach (var evt in history.Events) WriteEvent(writer, evt);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, WorkItemHistoryEvent evt)
    {
        writer.WriteStartObject();
        writer.WriteNumber("updateId", evt.UpdateId);
        writer.WriteNumber("revision", evt.Revision);

        if (evt.ChangedAt.HasValue)
            writer.WriteString("changedAt", evt.ChangedAt.Value.ToUniversalTime().ToString("o"));
        else
            writer.WriteNull("changedAt");

        if (evt.ChangedBy is not null)
            writer.WriteString("changedBy", evt.ChangedBy);
        else
            writer.WriteNull("changedBy");

        if (evt.Detailed && evt.ChangedByIdentity is not null)
            writer.WriteString("changedByIdentity", evt.ChangedByIdentity);

        writer.WriteStartArray("changed");
        foreach (var name in evt.ChangedFields) writer.WriteStringValue(name);
        writer.WriteEndArray();

        if (evt.Detailed)
        {
            writer.WriteStartObject("fields");
            foreach (var field in evt.Fields)
            {
                writer.WriteStartObject(field.ReferenceName);
                WriteNullableString(writer, "oldValue", field.OldValue);
                WriteNullableString(writer, "newValue", field.NewValue);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        WriteRelations(writer, evt);
        writer.WriteEndObject();
    }

    private static void WriteRelations(Utf8JsonWriter writer, WorkItemHistoryEvent evt)
    {
        writer.WriteStartArray("relations");
        foreach (var relation in evt.Relations)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", relation.Kind == RelationChangeKind.Added ? "added" : "removed");
            writer.WriteString("relationType", relation.RelationType);
            writer.WriteNumber("targetId", relation.TargetId);

            if (relation.Target is { } target)
            {
                writer.WriteStartObject("target");
                writer.WriteNumber("id", target.Id);
                WriteNullableString(writer, "title", target.Title);
                WriteNullableString(writer, "type", target.Type);
                WriteNullableString(writer, "state", target.State);
                writer.WriteBoolean("deleted", target.Deleted);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("target");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }
}
