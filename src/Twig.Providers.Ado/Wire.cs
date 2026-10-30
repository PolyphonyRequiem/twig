using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;

namespace Twig.Providers.Ado;

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class TwigJsonContext : JsonSerializerContext;

internal sealed class AdoFailure(Error error) : Exception(error.Message)
{
    internal Error Error { get; } = error;
}

internal static class Wire
{
    internal static string Text(JsonElement node, string key) => node.GetProperty(key).GetString() is { Length: > 0 } text
        && text.Trim() == text ? text : throw Invalid($"Missing/noncanonical {key}.");
    internal static bool Flag(JsonElement node, string key) => node.GetProperty(key).GetBoolean();
    internal static int Positive(JsonElement node, string key) => node.GetProperty(key).GetInt32() is > 0 and var n
        ? n : throw Invalid($"Invalid {key}.");
    internal static string Escape(string value) => Uri.EscapeDataString(value);
    internal static string GuidText(string value) => Guid.TryParse(value, out var guid) && guid != Guid.Empty
        ? guid.ToString("D") : throw Invalid("A native immutable GUID is required.");
    internal static AdoFailure Invalid(string message) => new(new(ErrorCode.Unavailable, message));
    internal static AdoFailure Refuse(ErrorCode code, string message) => new(new(code, message));

    internal static JsonElement[] Rows(JsonElement root)
    {
        var rows = root.GetProperty("value").EnumerateArray().ToArray();
        if (root.TryGetProperty("count", out var count) && count.GetInt32() != rows.Length)
            throw Invalid("Native page count does not match its rows.");
        return rows;
    }

    internal static void Unique(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in root.EnumerateObject())
            {
                if (!names.Add(member.Name)) throw Invalid("Duplicate native JSON member.");
                Unique(member.Value);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var member in root.EnumerateArray()) Unique(member);
    }

    internal static FieldKind Kind(JsonElement field) => Flag(field, "isIdentity")
        ? FieldKind.Identity : Text(field, "type") switch
        {
            "string" or "plainText" or "treePath" or "history" => FieldKind.Text,
            "html" => FieldKind.Html,
            "integer" => FieldKind.Integer,
            "double" => FieldKind.Decimal,
            "boolean" => FieldKind.Boolean,
            "dateTime" => FieldKind.Instant,
            _ => FieldKind.Raw,
        };

    internal static FieldValue Value(JsonElement value, FieldKind kind)
    {
        if (value.ValueKind == JsonValueKind.Null) return FieldValue.Null;
        return kind switch
        {
            FieldKind.Text => FieldValue.String(value.GetString()!),
            FieldKind.Html => FieldValue.RichText(value.GetString()!, FieldKind.Html),
            FieldKind.Integer => FieldValue.Integer(value.GetInt64()),
            FieldKind.Decimal => FieldValue.Decimal(value.GetDecimal()),
            FieldKind.Boolean => FieldValue.Bool(value.GetBoolean()),
            FieldKind.Instant => FieldValue.At(value.GetDateTimeOffset()),
            FieldKind.Identity when value.ValueKind == JsonValueKind.String => FieldValue.Identity(value.GetString()!),
            FieldKind.Identity => FieldValue.Identity(Text(value, "id")),
            _ => FieldValue.Raw(value.GetRawText(), kind.ToString()),
        };
    }

    internal static FieldValue RuleValue(string text, FieldKind kind) => kind switch
    {
        FieldKind.Text => FieldValue.String(text),
        FieldKind.Html => FieldValue.RichText(text, FieldKind.Html),
        FieldKind.Integer => FieldValue.Integer(long.Parse(text, CultureInfo.InvariantCulture)),
        FieldKind.Decimal => FieldValue.Decimal(decimal.Parse(text, CultureInfo.InvariantCulture)),
        FieldKind.Boolean => FieldValue.Bool(bool.Parse(text)),
        FieldKind.Instant => FieldValue.At(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)),
        FieldKind.Identity => FieldValue.Identity(text),
        _ => throw Invalid("Opaque rule value cannot be evaluated safely."),
    };

    internal static FieldValue RuleOperand(JsonElement value, FieldKind kind)
        => value.ValueKind == JsonValueKind.Null ? FieldValue.Null : RuleValue(value.GetString()!, kind);

    internal static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer)) write(json);
        return buffer.ToArray();
    }

    internal static void WriteValue(Utf8JsonWriter writer, FieldValue value)
    {
        switch (value.Kind)
        {
            case FieldKind.Null: writer.WriteNullValue(); break;
            case FieldKind.Text:
            case FieldKind.Html:
            case FieldKind.Identity: writer.WriteStringValue(value.Text); break;
            case FieldKind.Integer: writer.WriteNumberValue((long)value.Number); break;
            case FieldKind.Decimal: writer.WriteNumberValue(value.Number); break;
            case FieldKind.Boolean: writer.WriteBooleanValue(value.Boolean); break;
            case FieldKind.Instant: writer.WriteStringValue(value.Instant); break;
            default: throw Refuse(ErrorCode.Unsupported, "ADO cannot send this declared value/format without conversion.");
        }
    }

    internal static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
