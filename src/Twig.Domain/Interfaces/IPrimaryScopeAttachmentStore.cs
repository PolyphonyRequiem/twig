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
/// Every write MUST leave the pre-existing file intact on failure (temp write +
/// fsync + rename, §6.1). Callers rely on that so ineligible-type refusals never
/// touch the record.
/// </para>
/// </summary>
internal interface IPrimaryScopeAttachmentStore
{
    /// <summary>
    /// <c>true</c> when the invocation directory sits inside a managed twig worktree
    /// — i.e. §3.1 anchors resolve, <c>.twig/layout.json</c> is present, and the
    /// worktree fingerprint matches the checked-in record. <c>false</c> when the
    /// checkout is unmanaged (fresh clone with only <c>twig.json</c>, or outside a
    /// Git worktree entirely). Never throws.
    /// </summary>
    bool IsManagedWorktree();

    /// <summary>Read the current attachment. Returns a failure carrying the AB#736
    /// storage identifier when validation fails; returns an <see cref="PrimaryScopeAttachment.Empty(string)"/>
    /// record on a managed worktree that has not yet been attached.</summary>
    Task<Result<PrimaryScopeAttachment>> ReadAsync(CancellationToken ct = default);

    /// <summary>Write the given attachment atomically. Verifies that
    /// <see cref="PrimaryScopeAttachment.ConnectionRef"/> matches the value derived
    /// from the current <c>twig.json</c> (§9.3) and refuses with
    /// <c>attachment-connection-mismatch</c> otherwise.</summary>
    Task<Result> WriteAsync(PrimaryScopeAttachment attachment, CancellationToken ct = default);

    /// <summary>Explicit managed-init hook: creates the §6.3 marker files
    /// (<c>layout.json</c>, <c>worktree.json</c>, an empty
    /// <c>attachment.json</c>) for the current worktree. Idempotent. This
    /// is the sole route that writes marker files; write-time bootstrap is
    /// forbidden by §7.</summary>
    Task<Result> InitializeAsync(CancellationToken ct = default);

    /// <summary>Bind the given active-claim reference (opaque id + mint
    /// timestamp) onto the current attachment record without disturbing the
    /// primary-scope block. Realizes AB#737 §Interface's <c>link</c> at
    /// mint step 4 and reclaim step 4. The write is atomic (§6.1 temp +
    /// rename); a failure surfaces the AB#736 §8 identifier verbatim so the
    /// claim service maps it to
    /// <see cref="Twig.Domain.Services.Claims.ClaimMintOutcome.AttachmentLinkFailed"/>.</summary>
    Task<Result> LinkClaimAsync(string claimId, DateTimeOffset mintedAt, CancellationToken ct = default);

    /// <summary>Drop the active-claim reference from the current attachment
    /// record when it points at <paramref name="expectedClaimId"/>. Realizes
    /// AB#737 §Interface's <c>unlink</c> at release step 3. A mismatch is
    /// treated as already-unlinked (the release is idempotent from the
    /// attachment's perspective); a real storage failure surfaces the
    /// AB#736 §8 identifier verbatim so the claim service maps it to
    /// <see cref="Twig.Domain.Services.Claims.ClaimReleaseOutcome.AttachmentUnlinkFailed"/>.</summary>
    Task<Result> UnlinkClaimAsync(string expectedClaimId, CancellationToken ct = default);
}
