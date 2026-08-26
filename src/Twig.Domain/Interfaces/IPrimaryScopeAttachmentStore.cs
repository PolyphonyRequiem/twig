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
}
