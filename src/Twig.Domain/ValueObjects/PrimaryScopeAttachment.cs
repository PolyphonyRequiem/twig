namespace Twig.Domain.ValueObjects;

/// <summary>
/// The worktree-local attachment record: the connection binding fingerprint,
/// the (optional) primary scope, and the (optional) active-claim reference.
/// <para>
/// AB#738 owns <see cref="PrimaryScope"/>; the <see cref="ActiveClaim"/> block
/// is opaque and never minted by this ticket — AB#737 defines the shape and
/// AB#739 mints it. Both fields are independently nullable (§4.2.2 of the storage
/// design): a freshly initialized managed worktree carries neither.
/// </para>
/// <para>
/// AB#736 §9.3 fixes "consumers set one field without disturbing the other".
/// A scope-only write MUST carry the entire <see cref="ActiveClaim"/> block —
/// including its original mint timestamp — through unchanged so that
/// <see cref="PrimaryScopeAttachmentStore.WriteAsync"/> can serialize it
/// byte-identically. Reducing the reference to just its identifier lost the
/// mint timestamp and let a switch or detach rewrite claim-owned provenance
/// under AB#739's feet; the full record fixes that boundary.
/// </para>
/// </summary>
internal sealed record PrimaryScopeAttachment(
    string ConnectionRef,
    PrimaryScope? PrimaryScope,
    ActiveClaimReference? ActiveClaim)
{
    /// <summary>
    /// Returns an unattached record — no primary scope, no active claim — bound to
    /// the given connection. Used both by managed init (writes an empty record) and
    /// by explicit detach through the attachment service.
    /// </summary>
    public static PrimaryScopeAttachment Empty(string connectionRef) =>
        new(connectionRef, PrimaryScope: null, ActiveClaim: null);

    public PrimaryScopeAttachment WithPrimaryScope(PrimaryScope scope) =>
        this with { PrimaryScope = scope };

    public PrimaryScopeAttachment WithoutPrimaryScope() =>
        this with { PrimaryScope = null };
}
