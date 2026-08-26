using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Narrow injectable seam that answers whether a work-item type may be a primary
/// scope on this managed worktree. Consulted before any attach or switch write.
/// The type allow-set is process-agnostic: the default implementation reads
/// runtime configuration on the active connection's profile; tests substitute
/// their own decision without touching config.
/// <para>
/// A successful <see cref="Result{T}"/> carrying <c>true</c> permits the
/// attach/switch write; carrying <c>false</c> refuses with
/// <see cref="Services.Attachment.AttachmentFailure.IneligibleType"/>. A failure
/// <see cref="Result{T}"/> — carrying the <c>eligibility-unavailable</c>
/// identifier — signals that no allow-set is resolvable yet (the selected
/// profile does not publish one, and workspace config carries none): the gate
/// MUST refuse, not silently permit. A permissive default is exactly the
/// silent-widening this seam exists to prevent.
/// </para>
/// </summary>
internal interface IPrimaryScopeTypeEligibility
{
    Result<bool> Evaluate(WorkItemType type);
}
