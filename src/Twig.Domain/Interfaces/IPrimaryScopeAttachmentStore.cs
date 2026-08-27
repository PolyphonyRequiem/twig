using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The storage seam AB#738 consumes. Realizes §9.3 of the worktree storage
/// design (AB#736): read and write the worktree-local <see cref="PrimaryScopeAttachment"/>
/// atomically, and answer whether the current invocation directory is inside a
/// managed worktree at all.
/// <para>
/// Reads validate the layout marker, the worktree fingerprint, and the connection
/// ref (§6.4 steps 3–5). A named failure returned as <see cref="Result"/> error
/// carries the storage identifier verbatim so the service can route on it.
/// </para>
/// <para>
/// AB#739 adds an OS-visible cross-process coordination surface: every
/// read exposes the observed <c>revision</c> counter and every write
/// takes an <c>expectedRevision</c>. Link/unlink acquire an exclusive
/// file lock through the OS so a peer process in the same worktree
/// cannot race the read-verify-write sequence.
/// </para>
/// </summary>
internal interface IPrimaryScopeAttachmentStore
{
    bool IsManagedWorktree();

    Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default);

    /// <summary>Reads the attachment together with the on-disk revision
    /// counter so the caller can gate a subsequent link/unlink on that
    /// revision. AB#739 §Attachment lifecycle coordination — the counter
    /// is the cross-process CAS handle.</summary>
    Task<Result<VersionedPrimaryScopeAttachment>> ReadWithRevisionAsync(CancellationToken ct = default);

    /// <summary>Writes the attachment atomically. When
    /// <paramref name="expectedRevision"/> is non-negative the store
    /// verifies the on-disk revision matches under an OS-visible lock
    /// and refuses with <c>attachment-version-mismatch</c> otherwise.
    /// A negative expected revision skips the check.</summary>
    Task<Result> WriteAsync(PrimaryScopeAttachment attachment, long expectedRevision = -1, CancellationToken ct = default);

    Task<Result> InitializeAsync(CancellationToken ct = default);

    /// <summary>Bind the given active-claim reference. Under an
    /// OS-visible lock: reads the current attachment, verifies the
    /// caller-supplied <paramref name="expectedRevision"/> against the
    /// on-disk value, verifies the primary-scope block still matches
    /// (<paramref name="expectedPrimaryScopeKind"/>,
    /// <paramref name="expectedWorkItemId"/>), and writes atomically.
    /// AB#736 §8 identifiers surface verbatim so the claim service maps
    /// scope switches to <c>attachment-scope-mismatch</c>, lost updates
    /// to <c>attachment-version-mismatch</c>, and I/O to
    /// <c>attachment-link-failed</c>.</summary>
    Task<Result> LinkClaimAsync(
        string claimId,
        DateTimeOffset mintedAt,
        string expectedPrimaryScopeKind,
        int expectedWorkItemId,
        long expectedRevision,
        CancellationToken ct = default);

    /// <summary>Drop the active-claim reference under the same OS-visible
    /// lock discipline as link. Idempotent when the record already points
    /// at a different claim id.</summary>
    Task<Result> UnlinkClaimAsync(
        string expectedClaimId,
        long expectedRevision,
        CancellationToken ct = default);
}

/// <summary>Attachment plus its on-disk revision counter — the CAS handle
/// AB#739 lifecycle operations pass to
/// <see cref="IPrimaryScopeAttachmentStore.LinkClaimAsync"/> and
/// <see cref="IPrimaryScopeAttachmentStore.UnlinkClaimAsync"/>.</summary>
internal readonly record struct VersionedPrimaryScopeAttachment(
    PrimaryScopeAttachment Attachment,
    long Revision);
