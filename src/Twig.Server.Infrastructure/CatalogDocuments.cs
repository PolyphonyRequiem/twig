using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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
internal sealed record SourceDocument(int Version, Guid Id, string Label, string NativeLocator, BindingDocument Binding,
    bool AllowOffline, DateTimeOffset AdmittedOnlineAt)
{
    internal static SourceDocument From(RegisteredSource source) => new(1, source.Id, source.Label, source.NativeLocator,
        BindingDocument.From(source.Binding), source.AllowOffline, source.AdmittedOnlineAt);
    internal RegisteredSource ToDomain() => Version == 1
        ? new(Id, Label, NativeLocator, Binding.ToDomain(), AllowOffline, AdmittedOnlineAt)
        : throw new JsonException("Unsupported catalog source format.");
}
internal sealed record KeyDocument(Guid Local, string? Provider, string? Scope, string? Native)
{
    internal static KeyDocument From(ItemKey key) => new(key.LocalId, key.Provider?.Value, key.ResourceScope, key.NativeId);
    internal ItemKey ToCore() => Provider is null && Scope is null && Native is null
        ? ItemKey.Local(Local)
        : Local == Guid.Empty && Provider is not null && Scope is not null && Native is not null
            ? ItemKey.Remote(new(Provider), Scope, Native)
            : throw new JsonException("Invalid persisted selector identity.");
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
internal sealed record QueryDocument(QueryKind Kind, string? Field, ValueDocument? Value, QueryDocument[] Children)
{
    internal static QueryDocument From(Query query) => new(query.Kind, query.Field,
        query.Value is null ? null : ValueDocument.From(query.Value), query.Children.Select(From).ToArray());
    internal Query ToCore() => new(Kind, Field, Value?.ToCore(), Children.Select(c => c.ToCore()));
}
internal sealed record SelectorDocument(SelectorKind Kind, KeyDocument? Item, QueryDocument? Query, string? Hierarchy)
{
    internal static SelectorDocument From(Selector selector) => new(selector.Kind,
        selector.Item is null ? null : KeyDocument.From(selector.Item),
        selector.Query is null ? null : QueryDocument.From(selector.Query), selector.HierarchyRelation);
    internal Selector ToCore() => new(Kind, Item?.ToCore(), Query?.ToCore(), Hierarchy);
}
internal sealed record SelectionDocument(Guid SourceId, BindingDocument Binding, SelectorDocument[] Selectors)
{
    internal static SelectionDocument From(SourceSelection source) => new(source.SourceId,
        BindingDocument.From(source.Binding), source.Selectors.Select(SelectorDocument.From).ToArray());
    internal SourceSelection ToDomain() => new(SourceId, Binding.ToDomain(), Selectors.Select(s => s.ToCore()));
}
internal sealed record BenchDocument(int Version, Guid Id, string Name, long Revision, SelectionDocument[] Sources)
{
    internal static BenchDocument From(BenchDefinition bench) => new(1, bench.Id, bench.Name, bench.Revision,
        bench.Sources.Select(SelectionDocument.From).ToArray());
    internal BenchDefinition ToDomain() => Version == 1
        ? new(Id, Name, Revision, Sources.Select(s => s.ToDomain()))
        : throw new JsonException("Unsupported catalog Bench format.");
}
internal sealed record ReceiptDocument(int Version, Guid CatalogId, Guid CommandId, string Fingerprint, BenchDocument Definition, long CatalogRevision)
{
    internal static ReceiptDocument From(CreationReceipt receipt) => new(1, receipt.CatalogId, receipt.CommandId, receipt.Fingerprint,
        BenchDocument.From(receipt.Definition), receipt.CatalogRevision);
    internal CreationReceipt ToDomain()
    {
        var bench = Definition.ToDomain();
        if (Version != 1 || CatalogId == Guid.Empty || CommandId != bench.Id
            || Fingerprint != CatalogFingerprint.Creation(CatalogId, bench) || CatalogRevision < 1)
            throw new JsonException("Invalid catalog creation receipt.");
        return new(CatalogId, CommandId, Fingerprint, bench, CatalogRevision);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 64,
    GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SourceDocument))]
[JsonSerializable(typeof(BenchDocument))]
[JsonSerializable(typeof(ReceiptDocument))]
internal sealed partial class CatalogJson : JsonSerializerContext;
