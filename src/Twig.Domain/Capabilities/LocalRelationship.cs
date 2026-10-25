using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Capabilities;

/// <summary>
/// A Twig-owned relationship held in the durable local overlay. It is never stored in, or
/// replaced by, the rebuildable provider mirror, so remote refresh cannot erase it. Its
/// <see cref="Origin"/> is always <see cref="RelationshipOrigin.TwigLocal"/>.
/// </summary>
/// <remarks>
/// Endpoints are provider-neutral <see cref="LocalWorkItemId"/>s (Epic #1168), never provider ids.
/// <see cref="Revision"/> is a local compare-and-swap counter starting at 1, unrelated to any
/// provider revision token. A removed edge keeps its row with <see cref="DeletedAt"/> set, so a
/// stale writer cannot resurrect it. Related edges are stored once; see <see cref="Normalize"/>.
/// </remarks>
internal sealed record LocalRelationship(
    LocalWorkItemId Source,
    LocalWorkItemId Target,
    LinkKind Kind,
    EmulationStorage Storage,
    PublicationState Publication,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt = null)
{
    public RelationshipOrigin Origin => RelationshipOrigin.TwigLocal;

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// Related is symmetric, so it is stored once with the ordinally lesser persisted ("D")
    /// endpoint first. Directed kinds keep their order.
    /// </summary>
    public static (LocalWorkItemId Source, LocalWorkItemId Target) Normalize(
        LinkKind kind, LocalWorkItemId source, LocalWorkItemId target) =>
        kind == LinkKind.Related && string.CompareOrdinal(source.ToString(), target.ToString()) > 0
            ? (target, source)
            : (source, target);
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

/// <summary>
/// Durable storage for Twig-local relationships. Conflicts are results, not exceptions.
/// Every method normalizes a related pair, so callers may pass either order.
/// </summary>
internal interface ILocalRelationshipRepository
{
    /// <summary>Edges touching any of <paramref name="itemIds"/>, as source or target.</summary>
    Task<IReadOnlyList<LocalRelationship>> GetForItemsAsync(
        IReadOnlyCollection<LocalWorkItemId> itemIds, bool includeDeleted = false, CancellationToken ct = default);

    /// <summary>One edge, including a tombstone.</summary>
    Task<LocalRelationship?> GetAsync(
        LocalWorkItemId source, LocalWorkItemId target, LinkKind kind, CancellationToken ct = default);

    /// <summary>
    /// Writes a live edge. <paramref name="expectedRevision"/> null requires that no row exists
    /// (not even a tombstone); otherwise it must equal the stored revision. A new row starts at
    /// revision 1 and each accepted write increments it; the supplied
    /// <see cref="LocalRelationship.Revision"/> is ignored. Reviving a tombstone keeps its
    /// original <see cref="LocalRelationship.CreatedAt"/>.
    /// </summary>
    Task<LocalRelationshipWrite> UpsertAsync(
        LocalRelationship relationship, long? expectedRevision, CancellationToken ct = default);

    /// <summary>Marks an edge deleted when <paramref name="expectedRevision"/> matches.</summary>
    Task<LocalRelationshipWrite> TombstoneAsync(
        LocalWorkItemId source, LocalWorkItemId target, LinkKind kind, long expectedRevision,
        DateTimeOffset deletedAt, CancellationToken ct = default);
}
