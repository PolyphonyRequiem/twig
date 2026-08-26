namespace Twig.Domain.ValueObjects;

/// <summary>
/// The worktree-local attachment record: the connection binding fingerprint,
/// the (optional) primary scope, and the (optional) opaque local claim id.
/// <para>
/// AB#738 owns <see cref="PrimaryScope"/>; the <see cref="ActiveClaimId"/> field
/// is opaque and never minted by this ticket — AB#737 defines the shape and
/// AB#739 mints it. Both fields are independently nullable (§4.2.2 of the storage
/// design): a freshly initialized managed worktree carries neither.
/// </para>
/// </summary>
internal sealed record PrimaryScopeAttachment(
    string ConnectionRef,
    PrimaryScope? PrimaryScope,
    string? ActiveClaimId)
{
    /// <summary>
    /// Returns an unattached record — no primary scope, no active claim — bound to
    /// the given connection. Used both by managed init (writes an empty record) and
    /// by explicit detach through the attachment service.
    /// </summary>
    public static PrimaryScopeAttachment Empty(string connectionRef) =>
        new(connectionRef, PrimaryScope: null, ActiveClaimId: null);

    public PrimaryScopeAttachment WithPrimaryScope(PrimaryScope scope) =>
        this with { PrimaryScope = scope };

    public PrimaryScopeAttachment WithoutPrimaryScope() =>
        this with { PrimaryScope = null };
}
