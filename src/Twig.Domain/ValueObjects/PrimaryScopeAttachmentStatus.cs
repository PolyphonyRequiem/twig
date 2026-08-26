namespace Twig.Domain.ValueObjects;

/// <summary>
/// Read-only projection of the attachment state suitable for the status surfaces
/// (human <c>twig show</c> and machine <c>.twig/prompt.json</c>). Distinguishes:
/// <list type="bullet">
///   <item><description><see cref="IsManagedWorktree"/> = <c>false</c> and
///     <see cref="FailureCode"/> = <c>null</c>: the working directory is not
///     inside a managed twig worktree; the surfaces omit the attachment
///     block.</description></item>
///   <item><description><see cref="IsManagedWorktree"/> = <c>true</c> and
///     <see cref="PrimaryScope"/> = <c>null</c>: the checkout is explicitly not
///     attached; surfaces MUST state this.</description></item>
///   <item><description><see cref="PrimaryScope"/> non-<c>null</c>: surfaces render the
///     scope prominently, including <see cref="WorkItemTitle"/> when the local cache
///     knows it.</description></item>
///   <item><description><see cref="FailureCode"/> non-<c>null</c>: the underlying
///     storage read raised a named error (§8 of AB#736 — layout marker missing,
///     worktree-fingerprint-drift, attachment-connection-mismatch, and so on).
///     Surfaces MUST render the failure explicitly rather than silently degrade
///     to "unmanaged", so the operator sees the repair hint.</description></item>
/// </list>
/// The absence signal is intentional — the ticket demands unattached checkouts
/// state that fact explicitly rather than degrade silently.
/// </summary>
internal sealed record PrimaryScopeAttachmentStatus(
    bool IsManagedWorktree,
    PrimaryScope? PrimaryScope,
    string? WorkItemTitle,
    string? WorkItemType,
    string? FailureCode)
{
    public static PrimaryScopeAttachmentStatus NotManaged() =>
        new(IsManagedWorktree: false, PrimaryScope: null, WorkItemTitle: null, WorkItemType: null, FailureCode: null);

    public static PrimaryScopeAttachmentStatus Unattached() =>
        new(IsManagedWorktree: true, PrimaryScope: null, WorkItemTitle: null, WorkItemType: null, FailureCode: null);

    public static PrimaryScopeAttachmentStatus Attached(PrimaryScope scope, string? title, string? type) =>
        new(IsManagedWorktree: true, PrimaryScope: scope, WorkItemTitle: title, WorkItemType: type, FailureCode: null);

    /// <summary>
    /// Named-failure projection. <see cref="IsManagedWorktree"/> is <c>true</c>
    /// because a failure MUST be visible on a managed worktree; the surface
    /// renders <see cref="FailureCode"/> instead of an attached/unattached
    /// state so the operator can act on the repair hint.
    /// </summary>
    public static PrimaryScopeAttachmentStatus Failed(string failureCode) =>
        new(IsManagedWorktree: true, PrimaryScope: null, WorkItemTitle: null, WorkItemType: null, FailureCode: failureCode);
}
