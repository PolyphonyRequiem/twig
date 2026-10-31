using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Twig.Core;

public static partial class Core
{
    public static string Digest(Proposal proposal) => Hash(ProposalBytes(proposal));
    public static byte[] ProposalBytes(Proposal proposal) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("version", Proposal.FormatVersion);
        WriteConnection(w, proposal.Connection);
        w.WriteString("schema", proposal.SchemaFingerprint);
        w.WriteNumber("storeVersion", proposal.ExpectedStoreVersion);
        w.WriteStartArray("operations");
        foreach (var op in proposal.Operations)
        {
            w.WriteStartObject();
            w.WriteString("id", op.Id); w.WriteNumber("kind", (int)op.Kind);
            w.WritePropertyName("item"); WriteKey(w, op.Item);
            w.WritePropertyName("revision");
            if (op.ExpectedRevision is { } revision)
            {
                w.WriteStartObject(); w.WritePropertyName("item"); WriteKey(w, revision.Item);
                w.WriteString("token", revision.Token); w.WriteEndObject();
            }
            else w.WriteNullValue();
            w.WriteString("type", op.Type);
            WriteFields(w, op.Fields);
            w.WritePropertyName("other");
            if (op.Other is null) w.WriteNullValue(); else WriteKey(w, op.Other);
            w.WriteString("relation", op.Relation);
            w.WriteNumber("origin", (int)op.Origin);
            w.WriteNumber("concurrency", (int)op.Concurrency);
            if (op.ExpectedLocalVersion is { } localVersion) w.WriteNumber("localVersion", localVersion);
            else w.WriteNull("localVersion");
            w.WritePropertyName("body");
            if (op.Body is null) w.WriteNullValue(); else WriteValue(w, op.Body);
            w.WritePropertyName("resource");
            if (op.Resource is not { } resource) w.WriteNullValue();
            else
            {
                w.WriteStartArray(); w.WriteStringValue(resource.Relation); w.WriteStringValue(resource.Target);
                w.WriteStringValue(resource.Name); w.WriteStringValue(resource.Comment); w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    });

    public static string SchemaDigest(ProcessSchema schema) => Hash(Write(w =>
    {
        w.WriteStartObject(); w.WriteNumber("version", 1);
        WriteConnection(w, schema.Connection);
        w.WriteStartArray("types");
        foreach (var type in schema.Types.OrderBy(t => t.Reference, StringComparer.Ordinal))
        {
            w.WriteStartObject(); w.WriteString("ref", type.Reference); w.WriteString("stateField", type.StateField);
            w.WriteBoolean("hidden", type.Hidden);
            w.WriteStartArray("fields");
            foreach (var field in type.Fields.OrderBy(f => f.Reference, StringComparer.Ordinal))
            {
                w.WriteStartArray(); w.WriteStringValue(field.Reference);
                w.WriteNumberValue((int)field.Kind); w.WriteBooleanValue(field.ReadOnly); w.WriteEndArray();
            }
            w.WriteEndArray(); w.WriteStartArray("states");
            foreach (var state in type.States.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                w.WriteStartArray(); w.WriteStringValue(state.Name); w.WriteNumberValue((int)state.Category); w.WriteEndArray();
            }
            w.WriteEndArray(); w.WriteStartArray("transitions");
            foreach (var transition in type.Transitions.OrderBy(t => t.From, StringComparer.Ordinal).ThenBy(t => t.To, StringComparer.Ordinal))
            {
                w.WriteStartArray(); w.WriteStringValue(transition.From); w.WriteStringValue(transition.To); w.WriteEndArray();
            }
            w.WriteEndArray(); w.WriteStartArray("rules");
            // Rule order is retained: never infer commutativity for supplied values.
            foreach (var rule in type.Rules)
            {
                w.WriteStartObject(); w.WriteString("field", rule.Field); w.WriteNumber("action", (int)rule.Action);
                w.WriteBoolean("disabled", rule.Disabled);
                w.WriteStartArray("conditions");
                foreach (var condition in rule.Conditions)
                {
                    w.WriteStartArray(); w.WriteNumberValue((int)condition.Kind); w.WriteStringValue(condition.Field);
                    if (condition.Value is null) w.WriteNullValue(); else WriteValue(w, condition.Value);
                    w.WriteEndArray();
                }
                w.WriteEndArray(); w.WriteStartArray("values");
                foreach (var value in rule.Values) WriteValue(w, value);
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteStartArray("relations");
        foreach (var relation in schema.Relations.OrderBy(r => r.Reference, StringComparer.Ordinal))
        {
            w.WriteStartArray(); w.WriteStringValue(relation.Reference);
            w.WriteNumberValue((int)relation.Shape); w.WriteBooleanValue(relation.Directed); w.WriteEndArray();
        }
        w.WriteEndArray(); w.WriteStartArray("gaps");
        foreach (var gap in schema.Gaps.Order(StringComparer.Ordinal)) w.WriteStringValue(gap);
        w.WriteEndArray(); w.WriteEndObject();
    }));

    internal static string KeyText(ItemKey key) => Encoding.UTF8.GetString(Write(w => WriteKey(w, key)));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteConnection(Utf8JsonWriter w, Connection connection)
    {
        w.WriteStartObject("connection"); w.WriteString("provider", connection.Provider.Value);
        w.WriteString("scope", connection.ResourceScope); w.WriteString("destination", connection.Destination);
        w.WriteString("identity", connection.IdentityRef); w.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter w, ItemKey key)
    {
        w.WriteStartArray();
        if (key.IsLocal) { w.WriteStringValue("local"); w.WriteStringValue(key.LocalId.ToString("D")); }
        else
        {
            w.WriteStringValue("remote"); w.WriteStringValue(key.Provider!.Value);
            w.WriteStringValue(key.ResourceScope); w.WriteStringValue(key.NativeId);
        }
        w.WriteEndArray();
    }

    private static void WriteFields(Utf8JsonWriter w, FieldValues fields)
    {
        w.WriteStartObject("fields");
        foreach (var (key, value) in fields) { w.WritePropertyName(key); WriteValue(w, value); }
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, FieldValue value)
    {
        w.WriteStartArray(); w.WriteNumberValue((int)value.Kind);
        switch (value.Kind)
        {
            case FieldKind.Null: w.WriteNullValue(); break;
            case FieldKind.Integer: w.WriteNumberValue(decimal.ToInt64(value.Number)); break;
            case FieldKind.Decimal:
                w.WriteStringValue(value.Number.ToString("G29", CultureInfo.InvariantCulture)); break;
            case FieldKind.Boolean: w.WriteBooleanValue(value.Boolean); break;
            case FieldKind.Instant:
                w.WriteStringValue(value.Instant.ToString("O", CultureInfo.InvariantCulture)); break;
            case FieldKind.StringList:
                w.WriteStartArray();
                foreach (var item in value.Values) w.WriteStringValue(item);
                w.WriteEndArray(); break;
            case FieldKind.Raw:
                w.WriteStringValue(value.DataType); w.WriteStringValue(value.Text); break;
            default: w.WriteStringValue(value.Text); break;
        }
        w.WriteEndArray();
    }
}
