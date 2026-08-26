using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Narrow injectable seam that answers whether a work-item type may be a primary
/// scope on this managed worktree. Consulted before any attach or switch write.
/// The type allow-set is process-agnostic: the default implementation reads
/// runtime configuration on the active connection's profile; tests substitute
/// their own decision without touching config.
/// <para>
/// Refusal returns <c>false</c>; nothing is written and the attachment service
/// surfaces <see cref="Services.Attachment.AttachmentFailure.IneligibleType"/>.
/// A permissive configuration (no allow-set configured) accepts every type — the
/// gate refuses only when a caller has explicitly narrowed the set.
/// </para>
/// </summary>
internal interface IPrimaryScopeTypeEligibility
{
    bool IsEligible(WorkItemType type);
}
