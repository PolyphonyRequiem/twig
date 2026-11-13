using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bench.Application;
using Bench.Domain;
using Twig.Core;
using Twig.Store.Sqlite;

namespace Bench.Server;

internal static class ViewMapping
{
    internal static CatalogSummary Catalog(CatalogSnapshot value) => new(value.Id, value.Revision,
        value.Sources.Select(s => new SourceSummary(s.Id, s.Label, s.AllowOffline, s.AdmittedOnlineAt)).ToArray(),
        value.Benches.Select(b => new BenchSummary(b.Id, b.Name, b.Revision)).ToArray());
    internal static BenchViewDto View(BenchView value, CatalogSnapshot catalog)
        => ViewLabeled(value, catalog.Sources.ToDictionary(source => source.Id, source => source.Label));
    internal static BenchViewDto ViewLabeled(BenchView value, IReadOnlyDictionary<Guid, string> labels) => new(value.CatalogId, value.CatalogRevision,
        value.Definition.Id, value.Definition.Name, value.Sources.Select(source =>
        {
            var label = labels.GetValueOrDefault(source.SourceId) ?? "Unavailable source";
            var notes = source.Error is { } error ? source.Limitations.Add(error.Message) : source.Limitations;
            return new SourceViewDto(source.SourceId, label, source.Error?.Code.ToString(), notes.ToArray(),
                source.Observation?.Mode.ToString(), source.Observation?.CacheReadAt, source.Observation?.SchemaObservedAt,
                source.Observation?.CacheVersion,
                source.Items.Select(i =>
                {
                    var fields = i.EffectiveFields;
                    string? title = null;
                    var titleField = source.Observation?.Binding.Connection.Provider.Value switch
                    {
                        "ado" => "System.Title",
                        "github" => Twig.Providers.GitHub.GitHubProvider.Title,
                        "twig" => Twig.Providers.Native.NativeProvider.Title,
                        _ => null,
                    };
                    if (titleField is not null && fields.TryGetValue(titleField, out var observedTitle)) title = observedTitle.Text;
                    return new ItemDto(Key(i.Identity), Locator(i),
                        i.Type, title, i.StateField, i.BenchMember, i.HasDraft, i.RelationshipsComplete, Fields(i.CachedFields), Fields(i.ProposedFields), titleField,
                        ItemWebUrl(source, i), i.Identity.Key.IsLocal, i.IsExcluded)
                    {
                        Pin = source.Observation is { } observation
                            ? BenchCodec.SerializeSelectors([new(SelectorKind.Pin, i.Identity.Key,
                                connection: observation.Binding.Connection)]) : null,
                    };
                }).ToArray(),
                source.Fields.Select(f => new GroupFieldDto(f.SourceId, f.Reference, f.Choices.Select(c =>
                    new ChoiceDto(c.Type, Value(c.Value), c.Conditional)).ToArray(), f.CompositeValues)).ToArray(),
                source.Hierarchy.Available, source.Hierarchy.Relation,
                source.Hierarchy.Evidence.Select(e => new EdgeDto(Key(e.From), Key(e.To), e.Relation, e.Deleted, e.DraftIntent, e.Origin.ToString())
                    { LocalVersion = e.Origin == RelationshipOrigin.Local ? e.LocalVersion : null }).ToArray(),
                source.Hierarchy.Limitations.ToArray(), source.Observation?.Binding.Connection.Provider.Value,
                source.Types.IsDefault ? [] : source.Types.Select(t => new TypeDto(t.Reference, t.Presentation,
                    t.Fields.ToArray(), t.States.ToArray()) { Gaps = t.Gaps.ToArray() }).ToArray())
                { SchemaFingerprint = source.Observation?.SchemaFingerprint };
        }).ToArray());
    internal static string Key(QualifiedItem item)
    {
        var key = item.Key;
        var values = new[] { item.SourceId.ToString("D"), key.IsLocal ? "local" : "remote",
            key.LocalId.ToString("D"), key.Provider?.Value ?? "", key.ResourceScope ?? "", key.NativeId ?? "" };
        var json = JsonSerializer.Serialize(values, MappingJson.Default.StringArray);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
    private static string Locator(ItemView item)
    {
        var key = item.Identity.Key;
        if (key.IsLocal) return key.LocalId.ToString("D");
        if (key.Provider?.Value != "github") return key.NativeId!;
        return item.CachedFields.TryGetValue(Twig.Providers.GitHub.GitHubProvider.Number, out var number)
            && number.Kind == FieldKind.Integer && number.Number > 0 && number.Number <= int.MaxValue
                ? number.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"Issue number unavailable (native ID: {key.NativeId})";
    }
    private static string? ItemWebUrl(SourceView source, ItemView item)
    {
        if (item.Identity.Key.IsLocal || source.WebUrl is null) return null;
        var provider = source.Observation?.Binding.Connection.Provider.Value;
        if (provider == "ado" && int.TryParse(item.Identity.Key.NativeId,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
            return new Uri(new Uri(source.WebUrl), "_workitems/edit/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)).AbsoluteUri;
        if (provider == "github" && item.CachedFields.TryGetValue(Twig.Providers.GitHub.GitHubProvider.Number, out var number)
            && number.Kind == FieldKind.Integer && number.Number > 0 && number.Number <= int.MaxValue)
            return new Uri(new Uri(source.WebUrl), "issues/" + number.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).AbsoluteUri;
        return null;
    }
    private static FieldDto[] Fields(FieldValues fields) => fields.Select(f => new FieldDto(f.Key, Value(f.Value))).ToArray();
    internal static FieldValueDto Value(FieldValue value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            switch (value.Kind)
            {
                case FieldKind.Null: writer.WriteNullValue(); break;
                case FieldKind.Boolean: writer.WriteBooleanValue(value.Boolean); break;
                case FieldKind.Integer or FieldKind.Decimal:
                    writer.WriteStringValue(value.Number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)); break;
                case FieldKind.Instant: writer.WriteStringValue(value.Instant.ToString("O")); break;
                case FieldKind.StringList:
                    writer.WriteStartArray(); foreach (var item in value.Values) writer.WriteStringValue(item); writer.WriteEndArray(); break;
                default: writer.WriteStringValue(value.Text); break;
            }
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        return new(value.Kind.ToString(), json.RootElement.Clone(), value.DataType);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class MappingJson : System.Text.Json.Serialization.JsonSerializerContext;
