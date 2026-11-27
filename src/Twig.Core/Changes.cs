using System.Collections.Immutable;

namespace Twig.Core;

public enum OpKind { Create, Batch, Relate, Unrelate, Comment, Delete, LinkResource }
public enum ConcurrencyMode { ExactRevision, ObservedNonAtomic }
public enum OpStatus { Planned, Applying, Applied, Verified, Failed, Partial, Indeterminate }

public sealed record Op
{
    public Op(string id, OpKind kind, ItemKey item, Revision? expectedRevision = null,
        FieldValues? fields = null, string? type = null, ItemKey? other = null, string? relation = null,
        RelationshipOrigin origin = RelationshipOrigin.Provider, FieldValue? body = null,
        ConcurrencyMode concurrency = ConcurrencyMode.ExactRevision, long? expectedLocalVersion = null,
        ResourceLink? resource = null, IEnumerable<CreationLink>? links = null, FieldValues? createEvidence = null)
    {
        Id = Contract.Text(id, nameof(id));
        Kind = kind;
        Item = item ?? throw new ArgumentNullException(nameof(item));
        ExpectedRevision = expectedRevision;
        Fields = fields ?? FieldValues.Empty;
        Type = type;
        Other = other;
        Relation = relation;
        Origin = origin;
        Body = body;
        Concurrency = concurrency;
        ExpectedLocalVersion = expectedLocalVersion;
        Resource = resource;
        Links = Contract.Copy(links ?? []);
        CreateEvidence = createEvidence ?? FieldValues.Empty;
    }

    public string Id { get; private init; }
    public Op WithId(string id) => this with { Id = Contract.Text(id, nameof(id)) };
    public OpKind Kind { get; }
    public ItemKey Item { get; }
    public Revision? ExpectedRevision { get; }
    public FieldValues Fields { get; }
    public string? Type { get; }
    public ItemKey? Other { get; }
    public string? Relation { get; }
    public RelationshipOrigin Origin { get; }
    public FieldValue? Body { get; }
    public ConcurrencyMode Concurrency { get; }
    public long? ExpectedLocalVersion { get; }
    public ResourceLink? Resource { get; }
    public ImmutableArray<CreationLink> Links { get; }
    // Effective values assigned by the create endpoint, not independently writable fields.
    public FieldValues CreateEvidence { get; init; }
}

public sealed record GraphDependency(ItemKey Endpoint, string Fingerprint);
public sealed record ReviewBinding(Guid Revision, Guid Store, string Actor, string Capture)
{
    public ImmutableArray<GraphDependency> Graph { get; init; } = [];
}

public sealed record Plan
{
    public const int FormatVersion = 4;

    public Plan(Connection connection, string schemaFingerprint, long expectedStoreVersion, IEnumerable<Op> operations,
        string? policyIdentity = null, string? policyRevision = null, ReviewBinding? review = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        SchemaFingerprint = Contract.Text(schemaFingerprint, nameof(schemaFingerprint));
        // Kept for existing callers; only local store transactions use a version predicate.
        if (review is not null && (review.Revision == Guid.Empty || review.Store == Guid.Empty
            || string.IsNullOrWhiteSpace(review.Actor) || string.IsNullOrWhiteSpace(review.Capture)))
            throw new ArgumentException("Incomplete original review binding.", nameof(review));
        if (review is not null && (review.Graph.IsDefault || review.Graph.Any(d => d is null || d.Endpoint is null || string.IsNullOrWhiteSpace(d.Fingerprint))
            || review.Graph.Select(d => d.Endpoint).Distinct().Count() != review.Graph.Length))
            throw new ArgumentException("Invalid graph dependency set.", nameof(review));
        Review = review;
        Operations = Contract.Unique(operations, o => o.Id);
        if (Operations.IsEmpty) throw new ArgumentException("A plan must declare an effect.", nameof(operations));
        if ((policyIdentity is null) != (policyRevision is null))
            throw new ArgumentException("Configured policy identity and revision must be supplied together.", nameof(policyIdentity));
        PolicyIdentity = policyIdentity is null ? null : Contract.Text(policyIdentity, nameof(policyIdentity));
        PolicyRevision = policyRevision is null ? null : Contract.Text(policyRevision, nameof(policyRevision));
    }

    public Connection Connection { get; }
    public string SchemaFingerprint { get; }
    public ReviewBinding? Review { get; }
    public ImmutableArray<Op> Operations { get; }
    public string? PolicyIdentity { get; }
    public string? PolicyRevision { get; }
    public string Digest => Core.Digest(this);
}

public sealed record Receipt
{
    public Receipt(string digest, int operation, Guid attempt, OpStatus status, IEnumerable<string> evidence,
        Revision? revision = null, ItemKey? publishedKey = null, Error? error = null)
    {
        Digest = Contract.Text(digest, nameof(digest));
        if (operation < 0) throw new ArgumentOutOfRangeException(nameof(operation));
        if (attempt == Guid.Empty) throw new ArgumentException("A receipt needs its persisted attempt identity.", nameof(attempt));
        if (status is not (OpStatus.Applied or OpStatus.Verified or OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate))
            throw new ArgumentException("A receipt must describe an observed execution outcome.", nameof(status));
        Evidence = Contract.Copy(evidence);
        if (Evidence.IsEmpty || Evidence.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Outcome receipts require explicit evidence.", nameof(evidence));
        if (publishedKey?.IsLocal == true) throw new ArgumentException("A published key must be remote.", nameof(publishedKey));
        if (revision is not null && publishedKey is not null && revision.Item != publishedKey)
            throw new ArgumentException("Receipt revision and published identity disagree.", nameof(revision));
        if (status is OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate && error is null)
            throw new ArgumentException("Unsuccessful outcomes must carry a structured error.", nameof(error));
        if (status is OpStatus.Applied or OpStatus.Verified && error is not null)
            throw new ArgumentException("Acknowledgement/verification cannot hide an error.", nameof(error));
        (Operation, Attempt, Status, Revision, PublishedKey, Error) = (operation, attempt, status, revision, publishedKey, error);
    }

    public string Digest { get; }
    public int Operation { get; }
    public Guid Attempt { get; }
    public OpStatus Status { get; }
    public ImmutableArray<string> Evidence { get; }
    public Revision? Revision { get; }
    public ItemKey? PublishedKey { get; }
    public Error? Error { get; }
}

public sealed record JournalEntry
{
    private JournalEntry(string digest, int operation, Guid attempt, OpStatus status, ImmutableArray<Receipt> receipts)
        => (Digest, Operation, Attempt, Status, Receipts) = (digest, operation, attempt, status, receipts);

    public string Digest { get; }
    public int Operation { get; }
    public Guid Attempt { get; }
    public OpStatus Status { get; }
    public ImmutableArray<Receipt> Receipts { get; }

    public bool DefinitivelyHadNoEffect => Status == OpStatus.Failed && Receipts.Length == 1
        && Receipts[0] is { Status: OpStatus.Failed, PublishedKey: null, Revision: null, Error: not null } receipt
        && receipt.Digest == Digest && receipt.Operation == Operation && receipt.Attempt == Attempt;

    public bool CanRecheckAcknowledgedCreate => Status == OpStatus.Partial && Receipts.Length == 2
        && Receipts[0] is { Status: OpStatus.Applied, PublishedKey: not null } ack
        && Receipts[1].PublishedKey == ack.PublishedKey;

    internal bool VerifiesAcknowledgedCreate(Receipt receipt) => CanRecheckAcknowledgedCreate
        && receipt.Status == OpStatus.Verified && receipt.PublishedKey == Receipts[0].PublishedKey
        && receipt.Revision is not null && (Receipts[0].Revision is null || receipt.Revision == Receipts[0].Revision);

    public static JournalEntry Planned(string digest, int operation)
        => operation < 0 ? throw new ArgumentOutOfRangeException(nameof(operation))
            : new(Contract.Text(digest, nameof(digest)), operation, Guid.Empty, OpStatus.Planned, []);

    public Result<JournalEntry> Begin(Guid attempt) => Status != OpStatus.Planned || attempt == Guid.Empty
        ? Result<JournalEntry>.Fail(ErrorCode.Conflict, "Only an unattempted operation can begin; reconcile uncertain attempts without replay.")
        : Result<JournalEntry>.Ok(new(Digest, Operation, attempt, OpStatus.Applying, Receipts));

    public Result<JournalEntry> Record(Receipt receipt)
    {
        if (receipt.Digest != Digest || receipt.Operation != Operation || receipt.Attempt != Attempt)
            return Result<JournalEntry>.Fail(ErrorCode.Conflict, "Receipt does not belong to this exact operation attempt.");
        var permitted = Status == OpStatus.Applying && receipt.Status is OpStatus.Applied or OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate
            || Status == OpStatus.Applied && receipt.Status is OpStatus.Verified or OpStatus.Partial or OpStatus.Indeterminate
                && (receipt.PublishedKey is null || receipt.PublishedKey == Receipts[0].PublishedKey)
                && (receipt.Status != OpStatus.Verified || Receipts[0].PublishedKey is null
                    || receipt.PublishedKey == Receipts[0].PublishedKey
                        && (Receipts[0].Revision is null || receipt.Revision == Receipts[0].Revision))
            || VerifiesAcknowledgedCreate(receipt);
        return permitted
            ? Result<JournalEntry>.Ok(new(Digest, Operation, Attempt, receipt.Status, Receipts.Add(receipt)))
            : Result<JournalEntry>.Fail(ErrorCode.Conflict, "Illegal journal transition; applied is not verified, and unknown is not retryable.");
    }
}
