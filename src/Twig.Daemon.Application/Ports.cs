using Bench.Domain;
using System.Collections.Immutable;
using Twig.Core;

namespace Bench.Application;

public interface ICatalogStore
{
    Task<Result<CatalogSnapshot>> ReadAsync(CancellationToken ct);
    Task<Result<long>> RegisterAsync(RegisteredSource source, Guid expectedCatalogId, long expectedRevision, CancellationToken ct);
    Task<Result<CatalogCreation>> CreateAsync(CreateBenchCommand command, CancellationToken ct);
    Task<Result<CreationReceipt>> FindCreationAsync(Guid catalogId, Guid creationId, CancellationToken ct);
}

public sealed record CatalogCreation(CreationReceipt Receipt, bool Committed);
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

/// <summary>
/// Request-scoped typed reads only. Implementations retain no gate, transaction or credential lease after return.
/// A denied, changed or unknown binding MUST fail, never silently reuse stale evidence.
/// PermittedOfflineCache requires an unchanged admitted binding and explicit source-policy/user permission;
/// errors are native Result/ErrorCode values, never classified by prose matching.
/// </summary>
public interface ISourceReader
{
    Task<Result<SourceObservation>> AdmitAsync(string nativeLocator, CancellationToken ct);
    Task<Result<SourceObservation>> ReadAsync(RegisteredSource source, CancellationToken ct);
}

public sealed record NativeBenchSeed(Guid SourceId, Guid NativeBenchId);
