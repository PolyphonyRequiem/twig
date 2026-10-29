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
}

public sealed record QueryPage(ImmutableArray<WorkItem> Items, ImmutableArray<Relationship> Relationships,
    string? Continuation, bool Complete);

public interface IProvider
{
    ProviderCapabilities Capabilities { get; }
    Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct);
    Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct);
    Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct);

    // Exactly one already-journaled operation, never an opaque multi-request proposal execution.
    Task<Result<Receipt>> ApplyAsync(Proposal proposal, int operation, Guid attempt, CancellationToken ct);
    Task<Result<Receipt>> VerifyAsync(Proposal proposal, Receipt acknowledgement, CancellationToken ct);
}

public sealed record StoreSnapshot
{
    public StoreSnapshot(long version, IEnumerable<WorkItem>? items = null, IEnumerable<Relationship>? relationships = null,
        IEnumerable<Draft>? drafts = null, IEnumerable<Proposal>? proposals = null, IEnumerable<JournalEntry>? journal = null,
        IEnumerable<PublishMapping>? mappings = null, IEnumerable<Bench>? benches = null)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        Version = version;
        Items = Contract.Unique(items ?? [], i => Core.KeyText(i.Key));
        Relationships = Contract.Copy(relationships ?? []);
        Drafts = Contract.Unique(drafts ?? [], d => Core.KeyText(d.Key));
        Proposals = Contract.Unique(proposals ?? [], p => p.Digest);
        Journal = Contract.Copy(journal ?? []);
        Mappings = Contract.Copy(mappings ?? []);
        Benches = Contract.Unique(benches ?? [], b => b.Id.ToString("D"));
    }

    public long Version { get; }
    public ImmutableArray<WorkItem> Items { get; }
    public ImmutableArray<Relationship> Relationships { get; }
    public ImmutableArray<Draft> Drafts { get; }
    public ImmutableArray<Proposal> Proposals { get; }
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
