using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Attachment;

/// <summary>
/// Attach, switch, detach, and read the current managed worktree's single primary
/// scope. This is AB#738's deep module: every write flows through the same
/// eligibility gate and the same storage seam, and every code path that could
/// change the scope as a side effect refuses — the ticket's "explicit switch"
/// contract.
/// <para>
/// The service reads work-item type (for eligibility) and title (for the status
/// projection) through the local <see cref="IWorkItemRepository"/>. It never mints
/// a claim, never touches Twig Context (the active-item pointer lives in
/// <see cref="IContextStore"/>), and never routes claim reads through the claim
/// registry — <see cref="RequireActiveClaimForScopeAsync"/> observes the local
/// attachment reference and fails loud when it does not authorize the requested
/// scope. AB#739 will attach the mint/reclaim/release actions on top.
/// </para>
/// </summary>
/// <remarks>
/// The type is <c>internal</c> because the attachment surface is invoked through
/// DI from every surface (CLI status projection, MCP status tool, prompt writer)
/// and never rendered as a public verb. This satisfies the contract's "no
/// invented public verbs — expose a service interface through DI" rule.
/// </remarks>
internal sealed class PrimaryScopeAttachmentService
{
    private readonly IPrimaryScopeAttachmentStore _store;
    private readonly IPrimaryScopeTypeEligibility _eligibility;
    private readonly IWorkItemRepository _workItems;
    private readonly TimeProvider _clock;

    public PrimaryScopeAttachmentService(
        IPrimaryScopeAttachmentStore store,
        IPrimaryScopeTypeEligibility eligibility,
        IWorkItemRepository workItems,
        TimeProvider clock)
    {
        _store = store;
        _eligibility = eligibility;
        _workItems = workItems;
        _clock = clock;
    }

    /// <summary>
    /// Reads the current attachment status. Never fails on an unmanaged worktree
    /// — surfaces receive <see cref="PrimaryScopeAttachmentStatus.NotManaged"/> so
    /// they can omit the block cleanly. Managed but unattached becomes
    /// <see cref="PrimaryScopeAttachmentStatus.Unattached"/>, which the ticket
    /// requires to be stated explicitly.
    /// </summary>
    public async Task<Result<PrimaryScopeAttachmentStatus>> ReadStatusAsync(CancellationToken ct = default)
    {
        if (!_store.IsManagedWorktree())
            return Result.Ok(PrimaryScopeAttachmentStatus.NotManaged());

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Fail<PrimaryScopeAttachmentStatus>(read.Error);

        var attachment = read.Value;
        if (attachment.PrimaryScope is not { } scope)
            return Result.Ok(PrimaryScopeAttachmentStatus.Unattached());

        // Best-effort title/type enrichment: attachment MUST remain readable even
        // when the local cache does not know the work item. The ticket demands the
        // scope name appear prominently — but "unknown title" is a valid render
        // and MUST NOT flip the projection to "not attached".
        var item = await _workItems.GetByIdAsync(scope.WorkItemId, ct).ConfigureAwait(false);
        return Result.Ok(PrimaryScopeAttachmentStatus.Attached(
            scope,
            title: item?.Title,
            type: item?.Type.Value));
    }

    /// <summary>
    /// Attach the given work item as the primary scope. Refuses when the checkout
    /// is not a managed worktree, when a scope is already attached (require
    /// explicit <see cref="SwitchAsync"/>), when the work item is unknown, and
    /// when the profile's type allow-set rejects the type. No claim is minted.
    /// Nothing is written on any refusal path.
    /// </summary>
    public Task<Result> AttachAsync(int workItemId, CancellationToken ct = default) =>
        AttachInternalAsync(workItemId, allowReplace: false, ct);

    /// <summary>
    /// Explicit switch: replace the current primary scope with a new one. The
    /// separate verb exists precisely because AB#728 forbids implicit reassignment
    /// — <see cref="AttachAsync"/> refuses to overwrite; <see cref="SwitchAsync"/>
    /// is the sole route that changes the scope in place. The eligibility gate
    /// still runs; the new scope MUST be eligible.
    /// </summary>
    public Task<Result> SwitchAsync(int newWorkItemId, CancellationToken ct = default) =>
        AttachInternalAsync(newWorkItemId, allowReplace: true, ct);

    /// <summary>
    /// Detach: clear the primary scope on the current worktree. Refuses on an
    /// unmanaged worktree. If no scope is attached the call is a no-op success —
    /// detach is idempotent so a status recovery script does not have to
    /// pre-check. The claim reference field is preserved untouched: AB#738 never
    /// clears it (AB#739 owns claim lifecycle).
    /// </summary>
    public async Task<Result> DetachAsync(CancellationToken ct = default)
    {
        if (!_store.IsManagedWorktree())
            return NamedFailure(AttachmentFailure.NotManagedWorktree, string.Empty);

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var current = read.Value;
        if (current.PrimaryScope is null)
            return Result.Ok();

        var write = await _store.WriteAsync(current.WithoutPrimaryScope(), ct).ConfigureAwait(false);
        return write.IsSuccess ? Result.Ok() : NamedFailure(AttachmentFailure.StorageUnavailable, write.Error);
    }

    /// <summary>
    /// Fail-loud validation used by callers that require an active claim for a
    /// specific scope. Emits <see cref="AttachmentFailure.ScopeNotPrimary"/> when
    /// the requested scope is not this worktree's primary scope — the "parent
    /// attachment does not authorize child" boundary, named against the child
    /// id — and <see cref="AttachmentFailure.ClaimNotFoundForScope"/> when the
    /// primary scope carries no active claim reference. Never inspects a claim
    /// registry; that read is AB#739's.
    /// </summary>
    public async Task<Result> RequireActiveClaimForScopeAsync(int workItemId, CancellationToken ct = default)
    {
        if (!_store.IsManagedWorktree())
            return NamedFailure(AttachmentFailure.NotManagedWorktree, string.Empty);

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var attachment = read.Value;
        if (attachment.PrimaryScope is not { } scope)
            return NamedFailure(AttachmentFailure.NotAttached, $"#{workItemId}");

        if (scope.WorkItemId != workItemId)
            return NamedFailure(AttachmentFailure.ScopeNotPrimary,
                $"#{workItemId} is not the primary scope (attached: #{scope.WorkItemId}).");

        if (string.IsNullOrWhiteSpace(attachment.ActiveClaimId))
            return NamedFailure(AttachmentFailure.ClaimNotFoundForScope, $"#{workItemId}");

        return Result.Ok();
    }

    private async Task<Result> AttachInternalAsync(int workItemId, bool allowReplace, CancellationToken ct)
    {
        if (workItemId <= 0)
            return NamedFailure(AttachmentFailure.WorkItemUnknown, workItemId.ToString());

        if (!_store.IsManagedWorktree())
            return NamedFailure(AttachmentFailure.NotManagedWorktree, string.Empty);

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var current = read.Value;
        if (!allowReplace && current.PrimaryScope is not null)
            return NamedFailure(AttachmentFailure.AlreadyAttached,
                $"#{current.PrimaryScope.Value.WorkItemId}");

        var workItem = await _workItems.GetByIdAsync(workItemId, ct).ConfigureAwait(false);
        if (workItem is null)
            return NamedFailure(AttachmentFailure.WorkItemUnknown, $"#{workItemId}");

        if (!_eligibility.IsEligible(workItem.Type))
            return NamedFailure(AttachmentFailure.IneligibleType,
                $"work-item type '{workItem.Type.Value}' is not an eligible primary scope on this worktree.");

        var scope = new PrimaryScope(
            workItem.Id,
            WorkItemUrl: BuildWorkItemUrl(workItem.Id),
            AttachedAt: _clock.GetUtcNow());

        var next = current.WithPrimaryScope(scope);
        var write = await _store.WriteAsync(next, ct).ConfigureAwait(false);
        return write.IsSuccess ? Result.Ok() : NamedFailure(AttachmentFailure.StorageUnavailable, write.Error);
    }

    /// <summary>
    /// Renders a deterministic work-item URL. The value is opaque provenance for
    /// stolen-<c>.twig/</c> detection (§4.2.2), never a live navigation target.
    /// Kept process-agnostic — the connection binding is resolved from
    /// <c>twig.json</c> at storage-write time (§9.3) so this string does not
    /// duplicate the organization or project.
    /// </summary>
    private static string BuildWorkItemUrl(int workItemId) =>
        $"workitem:{workItemId}";

    private static Result NamedFailure(AttachmentFailure code, string detail) =>
        Result.Fail(string.IsNullOrEmpty(detail)
            ? code.ToString()
            : $"{code}: {detail}");
}
