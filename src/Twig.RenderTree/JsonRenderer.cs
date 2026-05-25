using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Twig.RenderTree;

/// <summary>
/// Renders a <see cref="RenderTree"/> as pretty-printed JSON suitable for piping
/// and automation. Manually written via <see cref="Utf8JsonWriter"/> so the
/// rendering is AOT-clean (no reflection).
/// </summary>
/// <remarks>
/// <para>Projection rules:</para>
/// <list type="bullet">
/// <item>A tree whose single root node is a <see cref="RenderNode.Record"/> projects
/// as a top-level JSON object whose properties are the record's
/// <see cref="RenderNode.Record.Fields"/>. The discriminator
/// <see cref="RenderNode.Record.Kind"/> is NOT emitted — it is a machine tag for
/// renderer dispatch, not a wire field. This matches the legacy
/// <c>JsonOutputFormatter.FormatSetConfirmation</c> shape that downstream
/// consumers (MCP envelopes, shell scripts) already depend on.</item>
/// <item>A tree whose single root node is a <see cref="RenderNode.Table"/> projects
/// as a top-level JSON array whose elements are the table's rows projected as
/// objects.</item>
/// <item>A tree with multiple root nodes — or a single root that is not a Record
/// or Table — projects as a top-level JSON array of per-node projections.</item>
/// </list>
/// <para>Per-node projection inside an array:</para>
/// <list type="bullet">
/// <item><see cref="RenderNode.Text"/> → <c>{ "text": "..." }</c>, plus
/// <c>"severity"</c> when non-<see cref="Severity.None"/>.</item>
/// <item><see cref="RenderNode.Hint"/> → omitted (hints are human-only).</item>
/// <item><see cref="RenderNode.KeyValue"/> → <c>{ "key": "...", "value": ... }</c>
/// with the value typed by <see cref="RenderCell.Value"/>.</item>
/// <item><see cref="RenderNode.Record"/> → object of fields; <c>kind</c>
/// emitted when set (inside an array, the kind tag carries meaning).</item>
/// <item><see cref="RenderNode.Table"/> → <c>{ "rows": [...] }</c>; columns are
/// metadata only, omitted from machine output (consumers use field keys).</item>
/// <item><see cref="RenderNode.TreeView"/> → hierarchical object; each branch is
/// <c>{ ...row.Cells, "children": [...] }</c>.</item>
/// <item><see cref="RenderNode.Section"/> → <c>{ "header": "...", "children": [...] }</c>;
/// header omitted when null.</item>
/// <item><see cref="RenderNode.Document"/> → object whose properties come from each
/// <see cref="DocumentField.Key"/>; the field's <see cref="DocumentField.Node"/> is
/// projected by its type (KeyValue → scalar, Table → array, Document → nested object).
/// Fields with <see cref="RenderAudience.HumanOnly"/> are skipped. A document at the
/// single root projects without its <c>kind</c> tag; inside an array the tag is
/// emitted as a discriminator (matching <see cref="RenderNode.Record"/>).</item>
/// </list>
/// <para>
/// <see cref="RenderValue"/> projection:
/// <c>Integer</c>→number, <c>Decimal</c>→number, <c>Boolean</c>→bool,
/// <c>String</c>→string, <c>DateTime</c>→ISO-8601 string, <c>Null</c>→null,
/// <c>Object</c>→nested JSON object whose properties are the contained cells,
/// <c>Absent</c>→property omitted (cells with <c>Absent</c> values do not appear
/// in machine output; the human renderer falls back to <see cref="RenderCell.DisplayText"/>).
/// </para>
/// </remarks>
public sealed class JsonRenderer : IRenderer
{
    private readonly TextWriter _output;
    private readonly JsonWriterOptions _writerOptions;

    /// <summary>Creates an indented (pretty-printed) JSON renderer.</summary>
    public JsonRenderer(TextWriter output) : this(output, indented: true) { }

    /// <summary>
    /// Creates a JSON renderer. Pass <paramref name="indented"/> = false for
    /// compact output (no whitespace beyond what JSON requires).
    /// </summary>
    public JsonRenderer(TextWriter output, bool indented)
    {
        _output = output;
        _writerOptions = new JsonWriterOptions { Indented = indented };
    }

    public void Render(RenderTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, _writerOptions))
        {
            this.WriteTree(writer, tree);
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        _output.Write(json);
    }

    private void WriteTree(Utf8JsonWriter writer, RenderTree tree)
    {
        if (tree.Nodes.Count == 1)
        {
            switch (tree.Nodes[0])
            {
                case RenderNode.Record rec:
                    WriteRecordFieldsAsObject(writer, rec, emitKind: false);
                    return;
                case RenderNode.Table table:
                    WriteTableRowsAsArray(writer, table);
                    return;
                case RenderNode.Document doc:
                    WriteDocumentAsObject(writer, doc, emitKind: false);
                    return;
            }
        }

        writer.WriteStartArray();
        foreach (var node in tree.Nodes)
        {
            WriteNodeAsArrayElement(writer, node);
        }
        writer.WriteEndArray();
    }

    private static void WriteNodeAsArrayElement(Utf8JsonWriter writer, RenderNode node)
    {
        switch (node)
        {
            case RenderNode.Text text:
                writer.WriteStartObject();
                writer.WriteString("text", text.Content);
                if (text.Severity != Severity.None)
                {
                    writer.WriteString("severity", text.Severity.ToString());
                }
                writer.WriteEndObject();
                break;
            case RenderNode.Markup markup:
                writer.WriteStartObject();
                writer.WriteString("text", MarkupHelpers.StripMarkup(markup.Content));
                writer.WriteEndObject();
                break;
            case RenderNode.Hint:
                // Hints are human-only — omitted from machine output.
                break;
            case RenderNode.KeyValue kv:
                writer.WriteStartObject();
                writer.WriteString("key", kv.Key);
                writer.WritePropertyName("value");
                WriteRenderValue(writer, kv.Value.Value, kv.Value.DisplayText);
                writer.WriteEndObject();
                break;
            case RenderNode.Record rec:
                WriteRecordFieldsAsObject(writer, rec, emitKind: true);
                break;
            case RenderNode.Table table:
                writer.WriteStartObject();
                writer.WritePropertyName("rows");
                WriteTableRowsAsArray(writer, table);
                writer.WriteEndObject();
                break;
            case RenderNode.TreeView treeView:
                WriteBranch(writer, treeView.Root);
                break;
            case RenderNode.Section section:
                writer.WriteStartObject();
                if (section.Header is not null)
                {
                    writer.WriteString("header", section.Header);
                }
                writer.WriteStartArray("children");
                foreach (var child in section.Children)
                {
                    WriteNodeAsArrayElement(writer, child);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case RenderNode.Document doc:
                WriteDocumentAsObject(writer, doc, emitKind: true);
                break;
        }
    }

    private static void WriteRecordFieldsAsObject(Utf8JsonWriter writer, RenderNode.Record rec, bool emitKind)
    {
        writer.WriteStartObject();
        if (emitKind && !string.IsNullOrEmpty(rec.Kind))
        {
            writer.WriteString("kind", rec.Kind);
        }

        foreach (var (key, cell) in rec.Fields)
        {
            if (cell.Value is RenderValue.Absent)
            {
                continue;
            }

            writer.WritePropertyName(key);
            WriteRenderValue(writer, cell.Value, cell.DisplayText);
        }

        writer.WriteEndObject();
    }

    private static void WriteTableRowsAsArray(Utf8JsonWriter writer, RenderNode.Table table)
    {
        writer.WriteStartArray();
        foreach (var row in table.Rows)
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(row.Kind))
            {
                writer.WriteString("kind", row.Kind);
            }

            foreach (var (key, cell) in row.Cells)
            {
                if (cell.Value is RenderValue.Absent)
                {
                    continue;
                }

                writer.WritePropertyName(key);
                WriteRenderValue(writer, cell.Value, cell.DisplayText);
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteBranch(Utf8JsonWriter writer, RenderTreeBranch branch)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrEmpty(branch.Row.Kind))
        {
            writer.WriteString("kind", branch.Row.Kind);
        }

        foreach (var (key, cell) in branch.Row.Cells)
        {
            if (cell.Value is RenderValue.Absent)
            {
                continue;
            }

            writer.WritePropertyName(key);
            WriteRenderValue(writer, cell.Value, cell.DisplayText);
        }

        if (branch.Children.Count > 0)
        {
            writer.WriteStartArray("children");
            foreach (var child in branch.Children)
            {
                WriteBranch(writer, child);
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteDocumentAsObject(Utf8JsonWriter writer, RenderNode.Document doc, bool emitKind)
    {
        writer.WriteStartObject();
        if (emitKind && !string.IsNullOrEmpty(doc.Kind))
        {
            writer.WriteString("kind", doc.Kind);
        }

        foreach (var field in doc.Fields)
        {
            if (field.Audience == RenderAudience.HumanOnly)
            {
                continue;
            }

            writer.WritePropertyName(field.Key);
            WriteDocumentFieldValue(writer, field.Node);
        }

        writer.WriteEndObject();
    }

    private static void WriteDocumentFieldValue(Utf8JsonWriter writer, RenderNode node)
    {
        switch (node)
        {
            case RenderNode.KeyValue kv:
                // Inside a Document, a KeyValue projects to its scalar value
                // — the surrounding field already carries the property name.
                WriteRenderValue(writer, kv.Value.Value, kv.Value.DisplayText);
                break;
            case RenderNode.Table table:
                // Inside a Document, a Table projects to the raw array of
                // rows (no enclosing object) — the surrounding field is the
                // named property.
                WriteTableRowsAsArray(writer, table);
                break;
            case RenderNode.Record rec:
                WriteRecordFieldsAsObject(writer, rec, emitKind: true);
                break;
            case RenderNode.Document nestedDoc:
                WriteDocumentAsObject(writer, nestedDoc, emitKind: true);
                break;
            case RenderNode.TreeView tv:
                WriteBranch(writer, tv.Root);
                break;
            case RenderNode.Section section:
                writer.WriteStartArray();
                foreach (var child in section.Children)
                {
                    WriteNodeAsArrayElement(writer, child);
                }
                writer.WriteEndArray();
                break;
            case RenderNode.Text text:
                writer.WriteStringValue(text.Content);
                break;
            case RenderNode.Markup markup:
                writer.WriteStringValue(MarkupHelpers.StripMarkup(markup.Content));
                break;
            case RenderNode.Hint:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteRenderValue(Utf8JsonWriter writer, RenderValue value, string displayFallback)
    {
        switch (value)
        {
            case RenderValue.String s:
                writer.WriteStringValue(s.Value);
                break;
            case RenderValue.Integer i:
                writer.WriteNumberValue(i.Value);
                break;
            case RenderValue.Decimal d:
                writer.WriteNumberValue(d.Value);
                break;
            case RenderValue.Boolean b:
                writer.WriteBooleanValue(b.Value);
                break;
            case RenderValue.DateTime dt:
                writer.WriteStringValue(dt.Value.ToString("o", CultureInfo.InvariantCulture));
                break;
            case RenderValue.Null:
                writer.WriteNullValue();
                break;
            case RenderValue.Absent:
                // Absent values are skipped by callers before reaching this method;
                // fall back to the display text so a stray Absent doesn't crash the
                // writer (e.g. KeyValue with Absent).
                writer.WriteStringValue(displayFallback);
                break;
            case RenderValue.Object obj:
                WriteObjectValue(writer, obj);
                break;
        }
    }

    private static void WriteObjectValue(Utf8JsonWriter writer, RenderValue.Object obj)
    {
        writer.WriteStartObject();
        foreach (var (key, cell) in obj.Cells)
        {
            if (cell.Value is RenderValue.Absent)
            {
                continue;
            }

            writer.WritePropertyName(key);
            WriteRenderValue(writer, cell.Value, cell.DisplayText);
        }
        writer.WriteEndObject();
    }
}
