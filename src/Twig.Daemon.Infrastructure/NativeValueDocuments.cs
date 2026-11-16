using System.Globalization;
using System.Text.Json;
using Bench.Domain;
using Twig.Core;

namespace Bench.Infrastructure;

internal static class PersistedJson
{
    internal static void Validate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
            { if (!names.Add(member.Name)) throw new JsonException("Duplicate persistence member."); Validate(member.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
            { if (child.ValueKind == JsonValueKind.Null) throw new JsonException("Null persistence collection element."); Validate(child); }
    }
}

// Versioned persistence projections only; construction/evaluation stays in the existing Core types.
internal sealed record BindingDocument(string Provider, string Scope, string Destination, string Identity,
    Guid StoreInstance, string NativeStamp)
{
    internal static BindingDocument From(SourceBinding value) => new(value.Connection.Provider.Value,
        value.Connection.ResourceScope, value.Connection.Destination, value.Connection.IdentityRef,
        value.StoreInstance, value.NativeStamp);
    internal SourceBinding ToDomain() => new(new(new(Provider), Scope, Destination, Identity), StoreInstance, NativeStamp);
}
internal sealed record ValueDocument(FieldKind Kind, JsonElement Value, string? DataType)
{
    internal static ValueDocument From(FieldValue value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value.Kind)
            {
                case FieldKind.Null: writer.WriteNullValue(); break;
                case FieldKind.Integer: writer.WriteNumberValue(decimal.ToInt64(value.Number)); break;
                case FieldKind.Decimal: writer.WriteStringValue(value.Number.ToString("G29", CultureInfo.InvariantCulture)); break;
                case FieldKind.Boolean: writer.WriteBooleanValue(value.Boolean); break;
                case FieldKind.Instant: writer.WriteStringValue(value.Instant.ToString("O", CultureInfo.InvariantCulture)); break;
                case FieldKind.StringList:
                    writer.WriteStartArray();
                    foreach (var item in value.Values) writer.WriteStringValue(item);
                    writer.WriteEndArray();
                    break;
                default: writer.WriteStringValue(value.Text); break;
            }
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return new(value.Kind, document.RootElement.Clone(), value.DataType);
    }
    internal FieldValue ToCore()
    {
        if (Kind != FieldKind.Raw && DataType is not null) throw new JsonException("Unexpected selector data type.");
        return Kind switch
        {
            FieldKind.Null when Value.ValueKind == JsonValueKind.Null => FieldValue.Null,
            FieldKind.Text => FieldValue.String(Value.GetString() ?? throw new JsonException()),
            FieldKind.Html or FieldKind.Markdown => FieldValue.RichText(Value.GetString() ?? throw new JsonException(), Kind),
            FieldKind.Integer => FieldValue.Integer(Value.GetInt64()),
            FieldKind.Decimal => FieldValue.Decimal(decimal.Parse(Value.GetString() ?? throw new JsonException(),
                NumberStyles.Number, CultureInfo.InvariantCulture)),
            FieldKind.Boolean => FieldValue.Bool(Value.GetBoolean()),
            FieldKind.Instant => FieldValue.At(DateTimeOffset.ParseExact(Value.GetString() ?? throw new JsonException(),
                "O", CultureInfo.InvariantCulture)),
            FieldKind.Identity => FieldValue.Identity(Value.GetString() ?? throw new JsonException()),
            FieldKind.StringList => FieldValue.Strings(Value.EnumerateArray().Select(v => v.GetString() ?? throw new JsonException())),
            FieldKind.Raw => FieldValue.Raw(Value.GetString() ?? throw new JsonException(), DataType ?? throw new JsonException()),
            _ => throw new JsonException("Invalid persisted typed selector value."),
        };
    }
}
