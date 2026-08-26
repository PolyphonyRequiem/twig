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
/// <para>
/// Every attach/switch/detach path runs the AB#736 §9.5 initialization contract
/// against the system store before touching the worktree-local file: the
/// worktree fingerprint MUST be registered with the same connection binding
/// (§9.4 <c>FindWorktree</c>), and a successful attach performs the required
/// <c>UpsertWorktree</c> after the write. A missing/retired registry row
/// surfaces as <see cref="AttachmentFailure.WorktreeNotRegistered"/> /
/// <see cref="AttachmentFailure.WorktreeRetired"/> and refuses fail-closed.
/// </para>
/// </summary>
/// <remarks>
/// The type is <c>internal</c> because the attachment surface is invoked through
/// DI from every surface (CLI status projection, MCP status tool, prompt writer)
/// and never rendered as a public verb; the surface-neutral seam is the public
/// <see cref="IPrimaryScopeAttachmentService"/> interface. This satisfies the
/// contract's "no invented public verbs — expose a service interface through
/// DI" rule.
/// </remarks>
internal sealed class PrimaryScopeAttachmentService : IPrimaryScopeAttachmentService
{
    private readonly IPrimaryScopeAttachmentStore _store;
    private readonly IPrimaryScopeTypeEligibility _eligibility;
    private readonly IWorkItemRepository _workItems;
    private readonly ISystemWorktreeRegistry _registry;
    private readonly IWorktreeFingerprintProvider _fingerprint;
    private readonly IPrimaryScopeUrlBuilder _urlBuilder;
    private readonly TimeProvider _clock;

    public PrimaryScopeAttachmentService(
        IPrimaryScopeAttachmentStore store,
        IPrimaryScopeTypeEligibility eligibility,
        IWorkItemRepository workItems,
        ISystemWorktreeRegistry registry,
        IWorktreeFingerprintProvider fingerprint,
        IPrimaryScopeUrlBuilder urlBuilder,
        TimeProvider clock)
    {
        _store = store;
        _eligibility = eligibility;
        _workItems = workItems;
        _registry = registry;
        _fingerprint = fingerprint;
        _urlBuilder = urlBuilder;
        _clock = clock;
    }

    /// <summary>
    /// Reads the current attachment status. Never fails on an unmanaged worktree
    /// — surfaces receive <see cref="PrimaryScopeAttachmentStatus.NotManaged"/> so
    /// they can omit the block cleanly. Managed but unattached becomes
    /// <see cref="PrimaryScopeAttachmentStatus.Unattached"/>, which the ticket
    /// requires to be stated explicitly. A named storage failure (§8) surfaces
    /// as <see cref="PrimaryScopeAttachmentStatus.Failed"/> carrying the
    /// identifier so the surface renders the repair hint rather than silently
    /// degrading to "unmanaged".
    /// </summary>
    public async Task<Result<PrimaryScopeAttachmentStatus>> ReadStatusAsync(CancellationToken ct = default)
    {
        if (!_store.IsManagedWorktree())
            return Result.Ok(PrimaryScopeAttachmentStatus.NotManaged());

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return Result.Ok(PrimaryScopeAttachmentStatus.Failed(read.Error));

        // §9.5 step 5 — the system-store registry MUST verify the current
        // fingerprint even on a read. A moved or unregistered worktree
        // surfaces the named failure on the status surface rather than
        // decaying to a plain "unattached" projection.
        var registry = await CheckSystemRegistryAsync(ct).ConfigureAwait(false);
        if (!registry.IsSuccess)
            return Result.Ok(PrimaryScopeAttachmentStatus.Failed(registry.Error));

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

        var registry = await CheckSystemRegistryAsync(ct).ConfigureAwait(false);
        if (!registry.IsSuccess)
            return NamedRegistryFailure(registry.Error);

        var read = await _store.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var current = read.Value.Attachment;
        if (current.PrimaryScope is null)
            return Result.Ok();

        // Scope-only write: MUST leave the ActiveClaim block byte-identical so
        // AB#739's mint timestamp / opaque id survives an AB#738 detach. Pass
        // the observed revision so a peer switch/link between our read and
        // our write surfaces as attachment-version-mismatch — a scope-only
        // write MUST NOT clobber a concurrent claim-lifecycle write.
        var write = await _store.WriteAsync(current.WithoutPrimaryScope(), expectedRevision: read.Value.Revision, ct).ConfigureAwait(false);
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

        var registry = await CheckSystemRegistryAsync(ct).ConfigureAwait(false);
        if (!registry.IsSuccess)
            return NamedRegistryFailure(registry.Error);

        var read = await _store.ReadAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var attachment = read.Value;
        if (attachment.PrimaryScope is not { } scope)
            return NamedFailure(AttachmentFailure.NotAttached, $"#{workItemId}");

        if (scope.WorkItemId != workItemId)
            return NamedFailure(AttachmentFailure.ScopeNotPrimary,
                $"#{workItemId} is not the primary scope (attached: #{scope.WorkItemId}).");

        if (attachment.ActiveClaim is null)
            return NamedFailure(AttachmentFailure.ClaimNotFoundForScope, $"#{workItemId}");

        return Result.Ok();
    }

    async Task<StatusProjection> IPrimaryScopeAttachmentService.ReadStatusAsync(CancellationToken ct)
    {
        var read = await ReadStatusAsync(ct).ConfigureAwait(false);
        // ReadStatusAsync itself never returns a failure Result — every named
        // storage failure is folded into a Failed StatusProjection above — so
        // the only branch here is the success projection.
        return ProjectStatus(read.Value);
    }

    private async Task<Result> AttachInternalAsync(int workItemId, bool allowReplace, CancellationToken ct)
    {
        if (workItemId <= 0)
            return NamedFailure(AttachmentFailure.WorkItemUnknown, workItemId.ToString());

        if (!_store.IsManagedWorktree())
            return NamedFailure(AttachmentFailure.NotManagedWorktree, string.Empty);

        var registry = await CheckSystemRegistryAsync(ct).ConfigureAwait(false);
        if (!registry.IsSuccess)
            return NamedRegistryFailure(registry.Error);

        var read = await _store.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, read.Error);

        var current = read.Value.Attachment;
        if (!allowReplace && current.PrimaryScope is not null)
            return NamedFailure(AttachmentFailure.AlreadyAttached,
                $"#{current.PrimaryScope.Value.WorkItemId}");

        var workItem = await _workItems.GetByIdAsync(workItemId, ct).ConfigureAwait(false);
        if (workItem is null)
            return NamedFailure(AttachmentFailure.WorkItemUnknown, $"#{workItemId}");

        var eligibility = _eligibility.Evaluate(workItem.Type);
        if (!eligibility.IsSuccess)
            return NamedFailure(AttachmentFailure.EligibilityUnavailable, eligibility.Error);
        if (!eligibility.Value)
            return NamedFailure(AttachmentFailure.IneligibleType,
                $"work-item type '{workItem.Type.Value}' is not an eligible primary scope on this worktree.");

        var scope = new PrimaryScope(
            workItem.Id,
            WorkItemUrl: _urlBuilder.BuildWorkItemUrl(workItem.Id),
            AttachedAt: _clock.GetUtcNow());

        // Scope-only write under expected-revision CAS. The observed
        // revision from ReadWithRevisionAsync is passed to WriteAsync so a
        // peer that raced a switch, link, or unlink between our read and
        // our write surfaces as attachment-version-mismatch — AB#736 §9.3
        // "consumers set one field without disturbing the other" is
        // realized cross-process, not just in-process.
        var next = current.WithPrimaryScope(scope);
        var write = await _store.WriteAsync(next, expectedRevision: read.Value.Revision, ct).ConfigureAwait(false);
        if (!write.IsSuccess)
            return NamedFailure(AttachmentFailure.StorageUnavailable, write.Error);
        return write;
    }

    /// <summary>
    /// AB#736 §9.5 step 5: the current worktree fingerprint MUST resolve to a
    /// non-retired system-store row bound to the same connectionRef. A missing
    /// row raises <c>worktree-not-registered</c>; a row with non-null
    /// <c>retiredAt</c> raises <c>worktree-retired</c>; a row bound to a
    /// different connection surfaces as <c>attachment-connection-mismatch</c>
    /// (same identifier the file-tier check uses).
    /// </summary>
    private async Task<Result> CheckSystemRegistryAsync(CancellationToken ct)
    {
        var fingerprint = _fingerprint.CurrentFingerprint;
        var row = await _registry.FindWorktreeAsync(fingerprint.CanonicalJson, ct).ConfigureAwait(false);
        if (!row.IsSuccess)
            return Result.Fail(row.Error);
        if (row.Value is null)
            return Result.Fail(AttachmentStorageFailure.WorktreeNotRegistered);
        if (row.Value.RetiredAt is not null)
            return Result.Fail(AttachmentStorageFailure.WorktreeRetired);
        if (!string.Equals(row.Value.ConnectionRef, fingerprint.ConnectionRef, StringComparison.Ordinal))
            return Result.Fail(AttachmentStorageFailure.AttachmentConnectionMismatch);
        return Result.Ok();
    }

    /// <summary>Projects the internal status record to the public
    /// <see cref="StatusProjection"/> the surfaces render.</summary>
    internal static StatusProjection ProjectStatus(PrimaryScopeAttachmentStatus status)
    {
        if (status.FailureCode is not null)
            return new StatusProjection(true, false, null, null, null, status.FailureCode);
        if (!status.IsManagedWorktree)
            return new StatusProjection(false, false, null, null, null);
        if (status.PrimaryScope is not { } scope)
            return new StatusProjection(true, false, null, null, null);
        return new StatusProjection(true, true, scope.WorkItemId, status.WorkItemTitle, status.WorkItemType);
    }

    private static Result NamedFailure(AttachmentFailure code, string detail) =>
        Result.Fail(string.IsNullOrEmpty(detail)
            ? code.ToString()
            : $"{code}: {detail}");

    /// <summary>
    /// Translates a raw AB#736 §8 identifier returned by
    /// <see cref="CheckSystemRegistryAsync"/> into a mutation-side named
    /// failure. The status-projection path passes the raw identifier straight
    /// through so the surface renders it verbatim; mutation callers instead
    /// route on the AB#738 <see cref="AttachmentFailure"/> enum for parity
    /// with the other mutation refusals.
    /// </summary>
    private static Result NamedRegistryFailure(string storageIdentifier) => storageIdentifier switch
    {
        AttachmentStorageFailure.WorktreeNotRegistered => NamedFailure(AttachmentFailure.WorktreeNotRegistered, storageIdentifier),
        AttachmentStorageFailure.WorktreeRetired => NamedFailure(AttachmentFailure.WorktreeRetired, storageIdentifier),
        _ => NamedFailure(AttachmentFailure.StorageUnavailable, storageIdentifier),
    };
}

/// <summary>
/// Snapshot of the current worktree fingerprint + resolved connection binding.
/// Held here as an injected seam so the attachment service can perform §9.5
/// checks without shelling out to git on every call and without the
/// service knowing how <c>twig.json</c> is loaded.
/// </summary>
internal readonly record struct WorktreeFingerprintContext(
    string CanonicalJson,
    string ConnectionRef,
    string WorktreeRoot);

/// <summary>Read-only accessor for the current worktree's canonical
/// fingerprint (§3.2 tuple + §5.1 connectionRef). Rebuilds every access so a
/// mid-process reconfiguration surfaces immediately.</summary>
internal interface IWorktreeFingerprintProvider
{
    WorktreeFingerprintContext CurrentFingerprint { get; }
}

/// <summary>Builds an origin-bearing ADO work-item URL from the checked-in
/// connection binding. AB#736 §4.2.2 requires the stored <c>workItemUrl</c> to
/// carry the organization/project origin so the file-tier consistency check
/// runs before the system store answers.</summary>
internal interface IPrimaryScopeUrlBuilder
{
    string BuildWorkItemUrl(int workItemId);
}
