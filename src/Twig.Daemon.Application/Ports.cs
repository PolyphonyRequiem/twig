using Bench.Domain;
using System.Collections.Immutable;
using Twig.Core;

namespace Bench.Application;

public enum ObservationMode { OnlineMetadata, PermittedOfflineCache, OnlineValidatedCache }

public sealed record SourceObservation(SourceBinding Binding, StoreSnapshot Snapshot, ProcessSchema Schema,
    DateTimeOffset CacheReadAt, DateTimeOffset SchemaObservedAt, ObservationMode Mode)
{
    public ImmutableArray<Selector> WorkspaceExclusions { get; init; } = [];
    public Guid? SelectedBenchId { get; init; }
    public PlanningSources? PlanningSources { get; init; }
    public DateTimeOffset? PlanningSourcesObservedAt { get; init; }
    public PlanningBrowse? PlanningBrowse { get; init; }
}
