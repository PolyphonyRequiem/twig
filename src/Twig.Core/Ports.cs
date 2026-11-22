using System.Collections.Immutable;

namespace Twig.Core;

public sealed record ProviderCapabilities
{
    public ProviderCapabilities(IEnumerable<OpKind> operations, IEnumerable<string> nativeRelations,
        IEnumerable<string> localRelations, bool exactRevisions, bool atomicFieldBatches)
    {
        Operations = Contract.Copy(operations).ToImmutableHashSet();
        NativeRelations = Contract.Copy(nativeRelations).ToImmutableHashSet(StringComparer.Ordinal);
        LocalRelations = Contract.Copy(localRelations).ToImmutableHashSet(StringComparer.Ordinal);
        ExactRevisions = exactRevisions;
        AtomicFieldBatches = atomicFieldBatches;
    }

    public ImmutableHashSet<OpKind> Operations { get; }
    public ImmutableHashSet<string> NativeRelations { get; }
    public ImmutableHashSet<string> LocalRelations { get; }
    public bool ExactRevisions { get; }
    public bool AtomicFieldBatches { get; }
    public bool AtomicRelationshipBatches { get; init; }
    public bool CreateFieldEvidence { get; init; }
}

public sealed record QueryPage(ImmutableArray<WorkItem> Items, ImmutableArray<Relationship> Relationships,
    string? Continuation, bool Complete);

public interface IProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<Result<ImmutableArray<IdentityProfile>>> ReadIdentityProfilesAsync(Connection connection, ImmutableArray<string> identities, CancellationToken ct)
        => Task.FromResult(Result<ImmutableArray<IdentityProfile>>.Fail(ErrorCode.Unsupported, "This provider does not expose identity profiles."));
    Task<Result<bool>> ValidateReadAccessAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(Result<bool>.Fail(ErrorCode.Unsupported, "This provider does not expose fresh native read-access validation."));
    Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct);
    Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct, bool includePresentation)
        => DiscoverSchemaAsync(connection, ct);
    Task<Result<FieldValues>> ResolveCreateEvidenceAsync(ProcessSchema schema, Op op, CancellationToken ct)
        => Task.FromResult(Result<FieldValues>.Ok(FieldValues.Empty));
    Task<Result<PlanningMetadata>> DiscoverPlanningAsync(Connection connection, string? team, CancellationToken ct)
        => Task.FromResult(Result<PlanningMetadata>.Fail(ErrorCode.Unsupported, "This provider does not expose native planning metadata."));
    Task<Result<ImmutableArray<PlanningTeam>>> DiscoverPlanningTeamsAsync(Connection connection, CancellationToken ct)
        => Task.FromResult(Result<ImmutableArray<PlanningTeam>>.Fail(ErrorCode.Unsupported, "This provider does not expose native project teams."));
    Task<Result<PlanningSources>> DiscoverPlanningSourcesAsync(Connection connection, string? team, CancellationToken ct)
        => Task.FromResult(Result<PlanningSources>.Fail(ErrorCode.Unsupported, "This provider does not expose saved queries or backlogs."));
    Task<Result<PlanningBrowsePage>> BrowsePlanningAsync(Connection connection, string? team, PlanningSourceKind kind, string id, string? expectedTeam, CancellationToken ct)
        => Task.FromResult(Result<PlanningBrowsePage>.Fail(ErrorCode.Unsupported, "This provider does not expose native planning membership."));
    Task<Result<PlanningBrowsePage>> BrowsePlanningAsync(Connection connection, string? team, PlanningSourceKind kind, string id,
        string? expectedTeam, ProcessSchema mappingSchema, CancellationToken ct)
        => Task.FromResult(Result<PlanningBrowsePage>.Fail(ErrorCode.Unsupported, "This provider does not expose native planning reads with recorded mapping metadata."));
    Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct);
    Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct, bool captureOnly = false);
    Task<Result<long>> ImportAsync(Plan plan, string confirmation, CancellationToken ct = default);

    // Exactly one already-journaled operation, never an opaque multi-request plan execution.
    Task<Result<Receipt>> ApplyAsync(Plan plan, int operation, Guid attempt, CancellationToken ct);
    Task<Result<Receipt>> VerifyAsync(Plan plan, Receipt acknowledgement, CancellationToken ct);
    Task<Result<Receipt>> RecoverAsync(Plan plan, int operation, CancellationToken ct = default);
}

public sealed record StoreSnapshot
{
    public StoreSnapshot(long version, IEnumerable<WorkItem>? items = null, IEnumerable<Relationship>? relationships = null,
        IEnumerable<RetainedItem>? retainedItems = null, IEnumerable<Plan>? plans = null, IEnumerable<JournalEntry>? journal = null,
        IEnumerable<PublishMapping>? mappings = null, IEnumerable<Bench>? benches = null)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        Items = Contract.Unique(items ?? [], i => Core.KeyText(i.Key));
        Relationships = Contract.Copy(relationships ?? []);
        RetainedItems = Contract.Unique(retainedItems ?? [], d => Core.KeyText(d.Key));
        Plans = Contract.Unique(plans ?? [], p => p.Digest);
        Journal = Contract.Copy(journal ?? []);
        Mappings = Contract.Copy(mappings ?? []);
        Benches = Contract.Unique(benches ?? [], b => b.Id.ToString("D"));
    }

    public long Version { get; }
    public ImmutableArray<WorkItem> Items { get; }
    public ImmutableArray<Relationship> Relationships { get; }
    public ImmutableArray<RetainedItem> RetainedItems { get; }
    public ImmutableArray<Plan> Plans { get; }
    public ImmutableArray<JournalEntry> Journal { get; }
    public ImmutableArray<PublishMapping> Mappings { get; }
    public ImmutableArray<Bench> Benches { get; }
}

public interface IStore
{
    Task<Result<StoreSnapshot>> ReadAsync(Connection connection, CancellationToken ct);

    // Compare-and-swap the coherent local state; no provider I/O inside this transaction.
    Task<Result<long>> CommitAsync(Connection connection, long expectedVersion, StoreSnapshot next, CancellationToken ct);
}
