using System.Collections.Immutable;
using Bench.Domain;
using Twig.Core;

namespace Bench.Application;

public sealed record QualifiedItem(Guid SourceId, ItemKey Key);
public sealed record BenchView(BenchDefinition Definition, Guid CatalogId, long CatalogRevision, ImmutableArray<SourceView> Sources);
public sealed record ObservationStamp(SourceBinding Binding, long CacheVersion, DateTimeOffset CacheReadAt,
    string SchemaFingerprint, DateTimeOffset SchemaObservedAt, ObservationMode Mode);
public sealed record ItemView(QualifiedItem Identity, string Type, bool BenchMember, bool HasDraft,
    bool RelationshipsComplete, FieldValues CachedFields, FieldValues ProposedFields, string? StateField = null)
{
    public FieldValues EffectiveFields => CachedFields.Overlay(ProposedFields);
    public bool IsExcluded { get; init; }
}
public sealed record FieldChoice(string Type, FieldValue Value, bool Conditional);
public sealed record EnumerableField(Guid SourceId, string Reference, ImmutableArray<FieldChoice> Choices,
    bool CompositeValues);
public sealed record HierarchyEdge(QualifiedItem From, QualifiedItem To, string Relation,
    RelationshipOrigin Origin, long LocalVersion, bool Deleted, bool DraftIntent);
public sealed record HierarchyView(bool Available, string? Relation, ImmutableArray<HierarchyEdge> Evidence,
    ImmutableArray<string> Limitations);
public sealed record TypeView(string Reference, TypePresentation? Presentation, ImmutableArray<FieldDef> Fields,
    ImmutableArray<State> States)
{
    public ImmutableArray<string> Gaps { get; init; } = [];
}
public sealed record SourceView(Guid SourceId, ObservationStamp? Observation, ImmutableArray<ItemView> Items,
    ImmutableArray<EnumerableField> Fields, HierarchyView Hierarchy, ImmutableArray<string> Limitations, Error? Error,
    ImmutableArray<TypeView> Types = default, string? WebUrl = null)
{
    public static SourceView Failed(Guid id, Error error) => new(id, null, [], [],
        new(false, null, [], ["Source evidence unavailable; no empty membership was inferred."]), [], error);
}
public sealed record ValueGroup(FieldValue? Value, bool Observed, bool InCatalog, bool Composite,
    ImmutableArray<QualifiedItem> Members);
