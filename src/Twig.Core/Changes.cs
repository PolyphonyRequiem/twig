using System.Collections.Immutable;

namespace Twig.Core;

public enum OpKind { Create, Batch, Relate, Unrelate, Comment, Delete }
public enum ConcurrencyMode { ExactRevision, ObservedNonAtomic }
public enum OpStatus { Planned, Applying, Applied, Verified, Failed, Partial, Indeterminate }

public sealed record Op
{
    public Op(string id, OpKind kind, ItemKey item, Revision? expectedRevision = null,
        FieldValues? fields = null, string? type = null, ItemKey? other = null, string? relation = null,
        RelationshipOrigin origin = RelationshipOrigin.Provider, FieldValue? body = null,
        ConcurrencyMode concurrency = ConcurrencyMode.ExactRevision, long? expectedLocalVersion = null)
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
    }

    public string Id { get; }
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
}

public sealed record Proposal
{
    public const int FormatVersion = 1;

    public Proposal(Connection connection, string schemaFingerprint, long expectedStoreVersion, IEnumerable<Op> operations)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        SchemaFingerprint = Contract.Text(schemaFingerprint, nameof(schemaFingerprint));
        if (expectedStoreVersion < 0) throw new ArgumentOutOfRangeException(nameof(expectedStoreVersion));
        ExpectedStoreVersion = expectedStoreVersion;
        Operations = Contract.Unique(operations, o => o.Id);
        if (Operations.IsEmpty) throw new ArgumentException("A proposal must declare an effect.", nameof(operations));
    }

    public Connection Connection { get; }
    public string SchemaFingerprint { get; }
    public long ExpectedStoreVersion { get; }
    public ImmutableArray<Op> Operations { get; }
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
            || Status == OpStatus.Applied && receipt.Status is OpStatus.Verified or OpStatus.Partial or OpStatus.Indeterminate;
        return permitted
            ? Result<JournalEntry>.Ok(new(Digest, Operation, Attempt, receipt.Status, Receipts.Add(receipt)))
            : Result<JournalEntry>.Fail(ErrorCode.Conflict, "Illegal journal transition; applied is not verified, and unknown is not retryable.");
    }
}
