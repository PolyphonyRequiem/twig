using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The current durable state of one Twig-owned local work item (Epic #1168).
/// </summary>
/// <param name="Id">The provider-neutral identity.</param>
/// <param name="Revision">
/// Twig's local compare-and-swap counter: 1 on creation, +1 on every accepted write. It is
/// independent of any provider's revision token.
/// </param>
/// <param name="Fields">The locally held field values.</param>
/// <param name="CreatedAt">When the identity was minted.</param>
/// <param name="UpdatedAt">When the last accepted write landed.</param>
/// <param name="DeletedAt">When the item was tombstoned; <see langword="null"/> while live.</param>
public sealed record LocalWorkItemRecord(
    LocalWorkItemId Id,
    long Revision,
    FieldValues Fields,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt)
{
    /// <summary>True once the item has been tombstoned.</summary>
    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>The write landed; <see cref="Record"/> is the new current state.</summary>
public sealed record LocalWriteApplied(LocalWorkItemRecord Record);

/// <summary>The caller's expected revision is stale; nothing was written.</summary>
public sealed record LocalRevisionConflict(LocalWorkItemRecord Current);

/// <summary>The item is tombstoned; tombstones are terminal, so nothing was written.</summary>
public sealed record LocalWorkItemTombstoned(LocalWorkItemRecord Current);

/// <summary>No item exists with the requested identity.</summary>
public sealed record LocalWorkItemNotFound(LocalWorkItemId Id);

/// <summary>The outcome of a compare-and-swap write to <see cref="ILocalWorkItemStore"/>.</summary>
public union LocalWriteResult(LocalWriteApplied, LocalRevisionConflict, LocalWorkItemTombstoned, LocalWorkItemNotFound);

/// <summary>
/// The durable, provider-neutral local work item store (Epic #1168).
/// </summary>
/// <remarks>
/// <para>
/// Holds current state plus tombstones only; no revision history is kept. Every write is a
/// compare-and-swap against <see cref="LocalWorkItemRecord.Revision"/>, and a stale write is
/// returned as <see cref="LocalRevisionConflict"/> rather than thrown or silently applied.
/// </para>
/// <para>
/// Rows live in the durable store, which is never dropped by a mirror rebuild. Writes join the
/// ambient unit-of-work transaction when one is active, so they commit atomically with the
/// proposal journal.
/// </para>
/// </remarks>
public interface ILocalWorkItemStore
{
    /// <summary>Mints a new identity holding <paramref name="fields"/> at revision 1.</summary>
    Task<LocalWorkItemRecord> CreateAsync(FieldValues fields, CancellationToken ct = default);

    /// <summary>Reads the current state, including a tombstone; <see langword="null"/> when unknown.</summary>
    Task<LocalWorkItemRecord?> GetAsync(LocalWorkItemId id, CancellationToken ct = default);

    /// <summary>Replaces the field values when <paramref name="expectedRevision"/> is current.</summary>
    Task<LocalWriteResult> UpdateAsync(LocalWorkItemId id, long expectedRevision, FieldValues fields, CancellationToken ct = default);

    /// <summary>Tombstones the item when <paramref name="expectedRevision"/> is current.</summary>
    Task<LocalWriteResult> DeleteAsync(LocalWorkItemId id, long expectedRevision, CancellationToken ct = default);
}
