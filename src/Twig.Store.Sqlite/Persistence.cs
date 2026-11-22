using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;

namespace Twig.Store.Sqlite;

internal static class Persistence
{
    internal static string ConnectionText(Connection connection)
        => JsonSerializer.Serialize(connection, TwigJsonContext.Default.Connection);

    internal static string Serialize(StoreSnapshot snapshot)
        => JsonSerializer.Serialize(SnapshotData.From(snapshot), TwigJsonContext.Default.SnapshotData);

    internal static StoreSnapshot Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        CheckDuplicates(document.RootElement);
        var data = JsonSerializer.Deserialize(json, TwigJsonContext.Default.SnapshotData)
            ?? throw new JsonException("Missing snapshot.");
        return data.ToCore();
    }

    internal static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate persistence member.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Null) throw new JsonException("Null persistence collection element.");
                CheckDuplicates(child);
            }
        }
    }

    internal static T Defined<T>(T value) where T : struct, Enum
        => Enum.IsDefined(value) ? value : throw new JsonException("Unknown persistence enum value.");
}

internal sealed record SnapshotData(int Version, long StoreVersion, ItemData[] Items, RelationshipData[] Relationships,
    DraftData[] Drafts, PlanData[] Plans, JournalData[] Journal, MappingData[] Mappings, BenchData[] Benches)
{
    internal static SnapshotData From(StoreSnapshot s) => new(4, s.Version,
        s.Items.Select(ItemData.From).ToArray(), s.Relationships.Select(RelationshipData.From).ToArray(),
        s.RetainedItems.Select(DraftData.From).ToArray(), s.Plans.Select(PlanData.From).ToArray(),
        s.Journal.Select(JournalData.From).ToArray(), s.Mappings.Select(MappingData.From).ToArray(),
        s.Benches.Select(BenchData.From).ToArray());

    internal StoreSnapshot ToCore()
    {
        if (Version != 4) throw new JsonException("Unsupported snapshot format version.");
        return new(StoreVersion, Items.Select(i => i.ToCore()), Relationships.Select(r => r.ToCore()),
            Drafts.Select(d => d.ToCore()), Plans.Select(p => p.ToCore()), Journal.Select(j => j.ToCore()),
            Mappings.Select(m => m.ToCore()), Benches.Select(b => b.ToCore()));
    }
}

internal sealed record KeyData(Guid LocalId, string? Provider, string? Scope, string? NativeId)
{
    internal static KeyData From(ItemKey k) => new(k.LocalId, k.Provider?.Value, k.ResourceScope, k.NativeId);
    internal ItemKey ToCore()
    {
        if (Provider is null && Scope is null && NativeId is null) return ItemKey.Local(LocalId);
        if (LocalId != Guid.Empty || Provider is null || Scope is null || NativeId is null)
            throw new JsonException("Invalid item identity shape.");
        return ItemKey.Remote(new ProviderId(Provider), Scope, NativeId);
    }
}

internal sealed record RevisionData(KeyData Item, string Token)
{
    internal static RevisionData? From(Revision? r) => r is null ? null : new(KeyData.From(r.Item), r.Token);
    internal Revision ToCore() => new(Item.ToCore(), Token);
}

internal sealed record ValueData(FieldKind Kind, string? Text, decimal Number, bool Boolean, DateTimeOffset Instant,
    string[] Values, string? DataType)
{
    internal static ValueData From(FieldValue v) => new(v.Kind, v.Text, v.Number, v.Boolean, v.Instant, v.Values.ToArray(), v.DataType);
    internal FieldValue ToCore()
    {
        var result = Kind switch
        {
            FieldKind.Null => FieldValue.Null,
            FieldKind.Text when Text is not null => FieldValue.String(Text),
            FieldKind.Html or FieldKind.Markdown when Text is not null => FieldValue.RichText(Text, Kind),
            FieldKind.Integer when Number == decimal.Truncate(Number) => FieldValue.Integer(decimal.ToInt64(Number)),
            FieldKind.Decimal => FieldValue.Decimal(Number),
            FieldKind.Boolean => FieldValue.Bool(Boolean),
            FieldKind.Instant => FieldValue.At(Instant),
            FieldKind.Identity when Text is not null => FieldValue.Identity(Text),
            FieldKind.StringList => FieldValue.Strings(Values),
            FieldKind.Raw when Text is not null && DataType is not null => FieldValue.Raw(Text, DataType),
            _ => throw new JsonException("Invalid persisted typed field."),
        };
        if (result.Text != Text || result.Number != Number || result.Boolean != Boolean
            || !result.Instant.EqualsExact(Instant) || result.DataType != DataType
            || !result.Values.SequenceEqual(Values, StringComparer.Ordinal))
            throw new JsonException("Unexpected payload on persisted field kind.");
        return result;
    }
}

internal sealed record FieldData(string Reference, ValueData Value)
{
    internal static FieldData[] From(FieldValues fields)
        => fields.Select(f => new FieldData(f.Key, ValueData.From(f.Value))).ToArray();
    internal static FieldValues ToCore(FieldData[] fields)
        => new(fields.Select(f => KeyValuePair.Create(f.Reference, f.Value.ToCore())));
}

internal sealed record ResourceData(string Relation, string Target, string? Name, string? Comment)
{
    internal static ResourceData From(ResourceLink r) => new(r.Relation, r.Target, r.Name, r.Comment);
    internal ResourceLink ToCore() => new(Relation, Target, Name, Comment);
}

internal sealed record ItemData(KeyData Key, string Type, FieldData[] Fields, RevisionData? Revision, bool RelationshipsComplete,
    ResourceData[] Resources, bool ResourcesComplete)
{
    internal static ItemData From(WorkItem i) => new(KeyData.From(i.Key), i.Type, FieldData.From(i.Fields),
        RevisionData.From(i.Revision), i.RelationshipsComplete, i.Resources.Select(ResourceData.From).ToArray(), i.ResourcesComplete);
    internal WorkItem ToCore() => new(Key.ToCore(), Type, FieldData.ToCore(Fields), Revision?.ToCore(),
        RelationshipsComplete, Resources.Select(r => r.ToCore()), ResourcesComplete);
}

internal sealed record RelationshipData(KeyData FromKey, KeyData ToKey, string Kind, RelationshipOrigin Origin,
    long LocalVersion, bool Deleted)
{
    internal static RelationshipData From(Relationship r)
        => new(KeyData.From(r.From), KeyData.From(r.To), r.Kind, r.Origin, r.LocalVersion, r.Deleted);
    internal Relationship ToCore()
        => new(FromKey.ToCore(), ToKey.ToCore(), Kind, Persistence.Defined(Origin), LocalVersion, Deleted);
}

internal sealed record DraftData(KeyData Key, string Type, ItemData? Baseline, FieldData[] Changes,
    KeyData? Parent = null, string? ParentRelation = null, ResourceData? Resource = null, CreationLinkData[]? Links = null)
{
    internal static DraftData From(RetainedItem item) => item switch
    {
        LocalEdits edits => new(KeyData.From(edits.Key), edits.Type, ItemData.From(edits.EditBaseline), FieldData.From(edits.Changes)),
        Seed seed => new(KeyData.From(seed.Key), seed.Type, null, FieldData.From(seed.Fields),
            seed.Parent is null ? null : KeyData.From(seed.Parent), seed.ParentRelation,
            seed.Resource is null ? null : ResourceData.From(seed.Resource),
            seed.Links.IsEmpty ? null : seed.Links.Select(CreationLinkData.From).ToArray()),
        _ => throw new ArgumentException("Unknown retained item kind.", nameof(item)),
    };

    internal RetainedItem ToCore()
    {
        var key = Key.ToCore();
        var fields = FieldData.ToCore(Changes);
        if (Baseline is null)
            return new Seed(key, Type, fields, Parent?.ToCore(), ParentRelation, Resource?.ToCore(),
                Links?.Select(link => link.ToCore()));
        if (Parent is not null || ParentRelation is not null || Resource is not null || Links is { Length: > 0 })
            throw new JsonException("Creation intent belongs to a seed.");
        return new LocalEdits(key, Type, Baseline.ToCore(), fields);
    }
}

internal sealed record CreationLinkData(KeyData Other, string Relation, bool Outgoing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Remove = false)
{
    internal static CreationLinkData From(CreationLink link) => new(KeyData.From(link.Other), link.Relation, link.Outgoing, link.Remove);
    internal CreationLink ToCore() => new(Other.ToCore(), Relation, Outgoing, Remove);
}

internal sealed record OpData(string Id, OpKind Kind, KeyData Item, RevisionData? ExpectedRevision, FieldData[] Fields,
    string? Type, KeyData? Other, string? Relation, RelationshipOrigin Origin, ValueData? Body,
    ConcurrencyMode Concurrency, long? ExpectedLocalVersion, ResourceData? Resource, CreationLinkData[]? Links = null,
    FieldData[]? CreateEvidence = null)
{
    internal static OpData From(Op o) => new(o.Id, o.Kind, KeyData.From(o.Item), RevisionData.From(o.ExpectedRevision),
        FieldData.From(o.Fields), o.Type, o.Other is null ? null : KeyData.From(o.Other), o.Relation, o.Origin,
        o.Body is null ? null : ValueData.From(o.Body), o.Concurrency, o.ExpectedLocalVersion,
        o.Resource is null ? null : ResourceData.From(o.Resource),
        o.Links.IsEmpty ? null : o.Links.Select(CreationLinkData.From).ToArray(),
        o.CreateEvidence.Count == 0 ? null : FieldData.From(o.CreateEvidence));
    internal Op ToCore() => new(Id, Persistence.Defined(Kind), Item.ToCore(), ExpectedRevision?.ToCore(),
        FieldData.ToCore(Fields), Type, Other?.ToCore(), Relation, Persistence.Defined(Origin), Body?.ToCore(),
        Persistence.Defined(Concurrency), ExpectedLocalVersion, Resource?.ToCore(), Links?.Select(link => link.ToCore()),
        CreateEvidence is null ? null : FieldData.ToCore(CreateEvidence));
}

internal sealed record PlanData(int Version, Connection Connection, string SchemaFingerprint, long ExpectedStoreVersion,
    OpData[] Operations, string Digest, string? PolicyIdentity, string? PolicyRevision)
{
    internal static PlanData From(Plan p)
        => new(Plan.FormatVersion, p.Connection, p.SchemaFingerprint, p.ExpectedStoreVersion,
            p.Operations.Select(OpData.From).ToArray(), p.Digest, p.PolicyIdentity, p.PolicyRevision);
    internal Plan ToCore()
    {
        if (Version != Plan.FormatVersion) throw new JsonException("Unsupported plan format version.");
        var plan = new Plan(Connection, SchemaFingerprint, ExpectedStoreVersion, Operations.Select(o => o.ToCore()),
            PolicyIdentity, PolicyRevision);
        if (plan.Digest != Digest) throw new JsonException("Retained plan digest does not match canonical intent.");
        return plan;
    }
}

internal sealed record ReceiptData(string Digest, int Operation, Guid Attempt, OpStatus Status, string[] Evidence,
    RevisionData? Revision, KeyData? PublishedKey, Error? Error)
{
    internal static ReceiptData From(Receipt r) => new(r.Digest, r.Operation, r.Attempt, r.Status, r.Evidence.ToArray(),
        RevisionData.From(r.Revision), r.PublishedKey is null ? null : KeyData.From(r.PublishedKey), r.Error);
    internal Receipt ToCore()
    {
        if (Error is not null) Persistence.Defined(Error.Code);
        return new(Digest, Operation, Attempt, Persistence.Defined(Status), Evidence, Revision?.ToCore(), PublishedKey?.ToCore(), Error);
    }
}

internal sealed record JournalData(string Digest, int Operation, Guid Attempt, OpStatus Status, ReceiptData[] Receipts)
{
    internal static JournalData From(JournalEntry j)
        => new(j.Digest, j.Operation, j.Attempt, j.Status, j.Receipts.Select(ReceiptData.From).ToArray());
    internal JournalEntry ToCore()
    {
        var row = JournalEntry.Planned(Digest, Operation);
        if (Attempt != Guid.Empty) row = Require(row.Begin(Attempt));
        foreach (var receipt in Receipts) row = Require(row.Record(receipt.ToCore()));
        if (row.Status != Status) throw new JsonException("Journal status disagrees with its append-only attempt evidence.");
        return row;
    }

    private static JournalEntry Require(Result<JournalEntry> result)
        => result.IsSuccess ? result.Value : throw new JsonException(result.Error.Message);
}

internal sealed record MappingData(KeyData Local, KeyData Remote, string Digest, int Operation)
{
    internal static MappingData From(PublishMapping m) => new(KeyData.From(m.Local), KeyData.From(m.Remote), m.PlanDigest, m.Operation);
    internal PublishMapping ToCore() => new(Local.ToCore(), Remote.ToCore(), Digest, Operation);
}

internal sealed record QueryData(QueryKind Kind, string? Field, ValueData? Value, QueryData[] Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlanningTeam = null)
{
    internal static QueryData From(Query q) => new(q.Kind, q.Field, q.Value is null ? null : ValueData.From(q.Value), q.Children.Select(From).ToArray(), q.PlanningTeam);
    internal Query ToCore() => new(Persistence.Defined(Kind), Field, Value?.ToCore(), Children.Select(c => c.ToCore()), PlanningTeam);
}

internal sealed record SelectorData(SelectorKind Kind, KeyData? Item, QueryData? Query, string? HierarchyRelation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Connection? Connection = null)
{
    internal static SelectorData From(Selector s) => new(s.Kind, s.Item is null ? null : KeyData.From(s.Item),
        s.Query is null ? null : QueryData.From(s.Query), s.HierarchyRelation, s.Connection);
    internal Selector ToCore() => new(Persistence.Defined(Kind), Item?.ToCore(), Query?.ToCore(), HierarchyRelation, Connection);
}

internal sealed record BenchData(Guid Id, string Name, SelectorData[] Selectors, Connection[] Connections,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Revision = 0)
{
    internal static BenchData From(Bench b) => new(b.Id, b.Name, b.Selectors.Select(SelectorData.From).ToArray(), b.Connections.ToArray(), b.Revision);
    internal Bench ToCore() => new(Id, Name, Selectors.Select(s => s.ToCore()), Revision, Connections);
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SnapshotData))]
[JsonSerializable(typeof(Connection))]
[JsonSerializable(typeof(ResourceData))]
[JsonSerializable(typeof(BenchData))]
[JsonSerializable(typeof(SelectorData[]))]
internal sealed partial class TwigJsonContext : JsonSerializerContext;
