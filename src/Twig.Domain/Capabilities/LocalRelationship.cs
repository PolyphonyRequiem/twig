using Twig.Domain.Enums;

namespace Twig.Domain.Capabilities;

/// <summary>
/// A Twig-owned relationship held in the durable local overlay. It is never stored in, or
/// replaced by, the rebuildable provider mirror, so remote refresh cannot erase it. Its
/// <see cref="Origin"/> is always <see cref="RelationshipOrigin.TwigLocal"/>.
/// </summary>
/// <remarks>
/// Endpoints are current Twig work-item ids; they move to the provider-neutral identity once
/// that contract exists. <see cref="Revision"/> is a local compare-and-swap counter, unrelated
/// to any provider revision token. A removed edge stays as a tombstone so a stale writer
/// cannot resurrect it.
/// </remarks>
internal sealed record LocalRelationship(
    int SourceId,
    int TargetId,
    LinkKind Kind,
    EmulationStorage Storage,
    PublicationState Publication,
    long Revision,
    bool Deleted,
    DateTimeOffset UpdatedAt)
{
    public RelationshipOrigin Origin => RelationshipOrigin.TwigLocal;
}

internal enum LocalRelationshipWriteStatus
{
    Written,
    Conflict,
}

/// <summary>
/// Outcome of a compare-and-swap write. <see cref="Current"/> is the stored row after a
/// successful write, or the row that blocked a conflicting one (null when it does not exist).
/// </summary>
internal sealed record LocalRelationshipWrite(LocalRelationshipWriteStatus Status, LocalRelationship? Current);

/// <summary>Durable storage for Twig-local relationships. Conflicts are results, not exceptions.</summary>
internal interface ILocalRelationshipRepository
{
    /// <summary>Edges touching any of <paramref name="itemIds"/>, as source or target.</summary>
    Task<IReadOnlyList<LocalRelationship>> GetForItemsAsync(
        IReadOnlyCollection<int> itemIds, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>One edge, including a tombstone.</summary>
    Task<LocalRelationship?> GetAsync(int sourceId, int targetId, LinkKind kind, CancellationToken ct = default);

    /// <summary>
    /// Writes a live edge. <paramref name="expectedRevision"/> null requires that no row exists
    /// (not even a tombstone); otherwise it must equal the stored revision. The stored revision
    /// is incremented; the supplied <see cref="LocalRelationship.Revision"/> is ignored.
    /// </summary>
    Task<LocalRelationshipWrite> UpsertAsync(
        LocalRelationship relationship, long? expectedRevision, CancellationToken ct = default);

    /// <summary>Marks an edge deleted when <paramref name="expectedRevision"/> matches.</summary>
    Task<LocalRelationshipWrite> TombstoneAsync(
        int sourceId, int targetId, LinkKind kind, long expectedRevision, DateTimeOffset updatedAt, CancellationToken ct = default);
}
