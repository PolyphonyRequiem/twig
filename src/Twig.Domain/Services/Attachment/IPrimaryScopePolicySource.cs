using Twig.Domain.Common;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Selected-profile policy source: the runtime seam AB#738's eligibility gate
/// consults to obtain the primary-scope type allow-set. The T1 §4.1 canonical
/// place to publish this is the checked-in <c>twig.json</c> policy block; once
/// AB#727 profile storage lands, the pinned profile itself SHOULD publish the
/// same block and this seam becomes the switch point.
/// <para>
/// A failure <see cref="Result{T}"/> — carrying an
/// <see cref="AttachmentStorageFailure.EligibilityUnavailable"/> identifier —
/// signals that no policy is resolvable yet. The eligibility gate MUST refuse
/// rather than silently permit; permit-by-default is the exact defect this
/// interface removes.
/// </para>
/// </summary>
internal interface IPrimaryScopePolicySource
{
    /// <summary>Return the case-insensitive allow-set of work-item type names
    /// permitted as this worktree's primary scope. An empty successful result
    /// is a valid "no type is eligible" answer (a repo may deliberately
    /// disable primary-scope attachment); a failure is
    /// <c>eligibility-unavailable</c>.</summary>
    Result<IReadOnlyList<string>> GetAllowSet();
}
