using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Domain.ValueObjects.Fields;

/// <summary>
/// The canonical JSON form of <see cref="FieldValues"/> (Epic #1168). Hand-written so it is
/// AOT- and trim-safe and so the wire form is a stated contract rather than a serializer
/// default.
/// </summary>
/// <remarks>
/// <para>
/// A map is an object keyed by reference name in ordinal order. Each value is an object whose
/// <b>first</b> property is <c>kind</c>, one of the exact, ordinal tokens <c>text</c>,
/// <c>rich-text</c>, <c>integer</c>, <c>decimal</c>, <c>boolean</c>, <c>date-time</c>,
/// <c>identity</c>, <c>string-list</c>, <c>null</c>, <c>raw</c>.
/// </para>
/// <para>
/// Reading is strict: an unknown or differently-cased token, an unknown, missing or repeated
/// property, or a duplicate key throws <see cref="JsonException"/> instead of guessing.
/// </para>
/// </remarks>
public sealed class FieldValuesJsonConverter : JsonConverter<FieldValues>
{
    private const string KindProperty = "kind";
    private const string ValueProperty = "value";
    private const string FormatProperty = "format";
    private const string NativeIdProperty = "nativeId";
    private const string DisplayNameProperty = "displayName";
    private const string DataTypeProperty = "dataType";

    /// <inheritdoc />
    public override FieldValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        var entries = new List<KeyValuePair<string, FieldValue>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Next(ref reader);
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            Expect(ref reader, JsonTokenType.PropertyName);
            var key = reader.GetString()!;
            if (!seen.Add(key))
                throw new JsonException($"Duplicate field reference name '{key}'.");
            Next(ref reader);
            entries.Add(new(key, ReadValue(ref reader)));
        }

        try
        {
            return FieldValues.Create(entries);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FieldValues value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, fieldValue) in value)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, fieldValue);
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes one value in canonical form.</summary>
    public static void WriteValue(Utf8JsonWriter writer, FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        switch (value.Value)
        {
            case TextValue t:
                writer.WriteString(KindProperty, "text");
                writer.WriteString(ValueProperty, t.Value);
                break;
            case RichTextValue r:
                writer.WriteString(KindProperty, "rich-text");
                writer.WriteString(FormatProperty, r.Format == RichTextFormat.Html ? "html" : "markdown");
                writer.WriteString(ValueProperty, r.Content);
                break;
            case IntegerValue i:
                writer.WriteString(KindProperty, "integer");
                writer.WriteNumber(ValueProperty, i.Value);
                break;
            case DecimalValue d:
                writer.WriteString(KindProperty, "decimal");
                writer.WriteNumber(ValueProperty, d.Value);
                break;
            case BooleanValue b:
                writer.WriteString(KindProperty, "boolean");
                writer.WriteBoolean(ValueProperty, b.Value);
                break;
            case DateTimeValue dt:
                writer.WriteString(KindProperty, "date-time");
                writer.WriteString(ValueProperty, dt.Value.ToString("O", CultureInfo.InvariantCulture));
                break;
            case IdentityValue id:
                writer.WriteString(KindProperty, "identity");
                writer.WriteString(NativeIdProperty, id.NativeId);
                if (id.DisplayName is not null)
                    writer.WriteString(DisplayNameProperty, id.DisplayName);
                break;
            case StringListValue list:
                writer.WriteString(KindProperty, "string-list");
                writer.WriteStartArray(ValueProperty);
                foreach (var item in list.Values)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
            case NullValue:
                writer.WriteString(KindProperty, "null");
                break;
            case RawValue raw:
                writer.WriteString(KindProperty, "raw");
                writer.WriteString(DataTypeProperty, raw.ProviderDataType);
                writer.WriteString(ValueProperty, raw.Raw);
                break;
            default:
                throw new JsonException("A field value carries no case and cannot be serialized.");
        }

        writer.WriteEndObject();
    }

    /// <summary>Reads one value in canonical form; the reader is positioned on its StartObject.</summary>
    public static FieldValue ReadValue(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        Next(ref reader);
        if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(KindProperty))
            throw new JsonException("A field value must begin with its 'kind' property.");
        Next(ref reader);
        Expect(ref reader, JsonTokenType.String);
        var kind = reader.GetString()!;

        try
        {
            FieldValue result = kind switch
            {
                "text" => new TextValue(ReadSingleString(ref reader, ValueProperty)),
                "rich-text" => ReadRichText(ref reader),
                "integer" => new IntegerValue(ReadSingleNumber(ref reader, static (ref Utf8JsonReader r) => r.GetInt64())),
                "decimal" => new DecimalValue(ReadSingleNumber(ref reader, static (ref Utf8JsonReader r) => r.GetDecimal())),
                "boolean" => new BooleanValue(ReadSingleBoolean(ref reader)),
                "date-time" => new DateTimeValue(ParseDateTime(ReadSingleString(ref reader, ValueProperty))),
                "identity" => ReadIdentity(ref reader),
                "string-list" => ReadStringList(ref reader),
                "null" => NullValue.Instance,
                "raw" => ReadRaw(ref reader),
                _ => throw new JsonException($"Unknown field value kind '{kind}'."),
            };

            Next(ref reader);
            Expect(ref reader, JsonTokenType.EndObject);
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new JsonException($"Invalid '{kind}' field value: {ex.Message}", ex);
        }
    }

    private delegate T NumberReader<T>(ref Utf8JsonReader reader);

    private static FieldValue ReadRichText(ref Utf8JsonReader reader)
    {
        var format = ReadProperty(ref reader, FormatProperty, JsonTokenType.String).GetString() switch
        {
            "html" => RichTextFormat.Html,
            "markdown" => RichTextFormat.Markdown,
            var other => throw new JsonException($"Unknown rich-text format '{other}'."),
        };
        var content = ReadProperty(ref reader, ValueProperty, JsonTokenType.String).GetString()!;
        return new RichTextValue(content, format);
    }

    private static FieldValue ReadIdentity(ref Utf8JsonReader reader)
    {
        var nativeId = ReadProperty(ref reader, NativeIdProperty, JsonTokenType.String).GetString()!;
        string? displayName = null;
        if (PeekProperty(ref reader, DisplayNameProperty))
        {
            Next(ref reader);
            Expect(ref reader, JsonTokenType.String);
            displayName = reader.GetString();
        }

        return new IdentityValue(nativeId, displayName);
    }

    private static FieldValue ReadStringList(ref Utf8JsonReader reader)
    {
        ReadProperty(ref reader, ValueProperty, JsonTokenType.StartArray);
        var items = new List<string>();
        while (true)
        {
            Next(ref reader);
            if (reader.TokenType == JsonTokenType.EndArray)
                break;
            Expect(ref reader, JsonTokenType.String);
            items.Add(reader.GetString()!);
        }

        return new StringListValue(items);
    }

    private static FieldValue ReadRaw(ref Utf8JsonReader reader)
    {
        var dataType = ReadProperty(ref reader, DataTypeProperty, JsonTokenType.String).GetString()!;
        var raw = ReadProperty(ref reader, ValueProperty, JsonTokenType.String).GetString()!;
        return new RawValue(raw, dataType);
    }

    private static string ReadSingleString(ref Utf8JsonReader reader, string name)
        => ReadProperty(ref reader, name, JsonTokenType.String).GetString()!;

    private static T ReadSingleNumber<T>(ref Utf8JsonReader reader, NumberReader<T> read)
    {
        ReadProperty(ref reader, ValueProperty, JsonTokenType.Number);
        return read(ref reader);
    }

    private static bool ReadSingleBoolean(ref Utf8JsonReader reader)
    {
        ReadPropertyName(ref reader, ValueProperty);
        Next(ref reader);
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => throw new JsonException("A boolean field value must be true or false."),
        };
    }

    private static DateTimeOffset ParseDateTime(string text)
        => DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static ref Utf8JsonReader ReadProperty(ref Utf8JsonReader reader, string name, JsonTokenType valueType)
    {
        ReadPropertyName(ref reader, name);
        Next(ref reader);
        Expect(ref reader, valueType);
        return ref reader;
    }

    private static void ReadPropertyName(ref Utf8JsonReader reader, string name)
    {
        Next(ref reader);
        if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals(name))
            throw new JsonException($"Expected property '{name}'.");
    }

    // Looks ahead without consuming: true and positioned on the property name when it matches.
    private static bool PeekProperty(ref Utf8JsonReader reader, string name)
    {
        var copy = reader;
        Next(ref copy);
        if (copy.TokenType != JsonTokenType.PropertyName || !copy.ValueTextEquals(name))
            return false;
        reader = copy;
        return true;
    }

    private static void Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
            throw new JsonException("Unexpected end of field values.");
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type)
            throw new JsonException($"Expected {type} but found {reader.TokenType}.");
    }
}
