namespace Twig.Domain.ValueObjects;

/// <summary>
/// Read-only projection of the attachment state suitable for the status surfaces
/// (human <c>twig show</c> and machine <c>.twig/prompt.json</c>). Distinguishes:
/// <list type="bullet">
///   <item><description><see cref="IsManagedWorktree"/> = <c>false</c>: the working directory
///     is not inside a managed twig worktree; the surfaces omit the attachment
///     block.</description></item>
///   <item><description><see cref="IsManagedWorktree"/> = <c>true</c> and
///     <see cref="PrimaryScope"/> = <c>null</c>: the checkout is explicitly not
///     attached; surfaces MUST state this.</description></item>
///   <item><description><see cref="PrimaryScope"/> non-<c>null</c>: surfaces render the
///     scope prominently, including <see cref="WorkItemTitle"/> when the local cache
///     knows it.</description></item>
/// </list>
/// The absence signal is intentional — the ticket demands unattached checkouts
/// state that fact explicitly rather than degrade silently.
/// </summary>
internal sealed record PrimaryScopeAttachmentStatus(
    bool IsManagedWorktree,
    PrimaryScope? PrimaryScope,
    string? WorkItemTitle,
    string? WorkItemType)
{
    public static PrimaryScopeAttachmentStatus NotManaged() =>
        new(IsManagedWorktree: false, PrimaryScope: null, WorkItemTitle: null, WorkItemType: null);

    public static PrimaryScopeAttachmentStatus Unattached() =>
        new(IsManagedWorktree: true, PrimaryScope: null, WorkItemTitle: null, WorkItemType: null);

    public static PrimaryScopeAttachmentStatus Attached(PrimaryScope scope, string? title, string? type) =>
        new(IsManagedWorktree: true, PrimaryScope: scope, WorkItemTitle: title, WorkItemType: type);
}
