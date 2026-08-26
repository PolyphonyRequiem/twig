using System.Text.Json;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Services.Claims;

/// <summary>
/// Concrete implementation of <see cref="ILocalClaimService"/> — the AB#739
/// deep module. Owns the T2 lifecycle end-to-end:
/// <list type="bullet">
///   <item>Mint: local pending reservation → ADO holder projection → CAS
///     activation → attachment link. Any failure at steps 2–4 rewrites the
///     pending row to the exact terminal state AB#737 §Mint fixes without
///     mutating any pre-existing conformant claim on another scope.</item>
///   <item>Reclaim (allowSupersede=false): re-runs mint over a released or
///     absent tuple. AllowSupersede=true: adds an atomic supersession CAS
///     inside step 3 through
///     <see cref="ISystemWorktreeRegistry.SupersedeAndActivateClaimAsync"/>.</item>
///   <item>Release: ADO clear → CAS terminalize → attachment unlink. ADO
///     clear failure leaves the row <see cref="ClaimStates.Active"/>. Local
///     CAS failure leaves both ADO cleared and the row active until the next
///     retry — the caller re-reads and decides.</item>
///   <item>Validate: pure local read. Reject a missing / non-active /
///     tuple-mismatched row loudly; never infer from
///     <c>System.AssignedTo</c> or any other ADO field.</item>
///   <item>Update-label: CAS-guarded label + notes rewrite. Never changes
///     state or minted timestamps.</item>
/// </list>
/// The service never mutates Twig Context, backlog hierarchy/rank, or branch
/// links; the only ADO write it performs is
/// <see cref="AdoClaimProjection.AssignedToField"/> via
/// <see cref="IAdoClaimProjection"/>.
/// </summary>
internal sealed class LocalClaimService : ILocalClaimService
{
    private static readonly IReadOnlyList<string> ReservedStates = new[] { ClaimStates.Pending, ClaimStates.Active };

    private readonly ISystemWorktreeRegistry _registry;
    private readonly IPrimaryScopeAttachmentStore _attachment;
    private readonly IClaimIdGenerator _idGen;
    private readonly IClaimCasTokenGenerator _casGen;
    private readonly IClaimHolderResolver _holderResolver;
    private readonly TimeProvider _clock;

    public LocalClaimService(
        ISystemWorktreeRegistry registry,
        IPrimaryScopeAttachmentStore attachment,
        IClaimIdGenerator idGen,
        IClaimCasTokenGenerator casGen,
        IClaimHolderResolver holderResolver,
        TimeProvider clock)
    {
        _registry = registry;
        _attachment = attachment;
        _idGen = idGen;
        _casGen = casGen;
        _holderResolver = holderResolver;
        _clock = clock;
    }

    // ── Mint ─────────────────────────────────────────────────────────

    public async Task<ClaimMintOutcome> MintAsync(MintClaimInput input, CancellationToken ct = default)
    {
        if (!ValidateMintInput(input, out var invalidReason))
            return new ClaimMintOutcome.InvalidRequest(invalidReason);
        if (!TryParseWorkItemId(input.PrimaryScopeId, out var workItemId))
            return new ClaimMintOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        // Resolve and finalize the holder identity supplied by the caller —
        // an empty identity trips fail-loud with HolderUnavailable rather
        // than silently defaulting to a system user.
        var holder = new ClaimHolderDescriptor(input.HolderIdentity, input.HolderDisplay);
        if (string.IsNullOrWhiteSpace(holder.Identity))
        {
            var resolved = await _holderResolver.ResolveAsync(ct).ConfigureAwait(false);
            if (!resolved.IsSuccess)
                return new ClaimMintOutcome.HolderUnavailable(resolved.Error);
            holder = resolved.Value;
        }

        // Step 1 — reservation + insert (CAS-guarded by the storage unique
        // index). AB#737 §Mint ordering step 1.
        var reservation = await ReservePendingAsync(input, holder, workItemId, ct).ConfigureAwait(false);
        if (reservation.Outcome is { } duplicateOrError)
            return duplicateOrError;
        var pending = reservation.Claim!;

        // Step 2 — ADO holder projection. On any failure, mint-abort:
        // terminalize the pending row as `released/mint-abort` under CAS and
        // return AdoProjectionFailed. Any pre-existing conformant claim on
        // another scope is untouched (mint never mutates unrelated rows).
        var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
        if (!projected.IsSuccess)
        {
            await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, ct).ConfigureAwait(false);
            return new ClaimMintOutcome.AdoProjectionFailed(projected.Error);
        }

        // Step 3 — activation under CAS. AB#737 §Mint step 3.
        var activation = await ActivatePendingAsync(pending, ct).ConfigureAwait(false);
        if (activation.Outcome is { } activationError)
            return activationError;
        var active = activation.Claim!;

        // Step 4 — attachment linkage. AB#737 §Mint step 4.
        var linked = await _attachment.LinkClaimAsync(active.ClaimId, active.ActivatedAt!.Value, ct).ConfigureAwait(false);
        if (!linked.IsSuccess)
            return new ClaimMintOutcome.AttachmentLinkFailed(linked.Error, active);
        return new ClaimMintOutcome.Succeeded(active);
    }

    // ── Reclaim ───────────────────────────────────────────────────────

    public async Task<ClaimReclaimOutcome> ReclaimAsync(ReclaimClaimInput input, CancellationToken ct = default)
    {
        if (!ValidateReclaimInput(input, out var invalidReason))
            return new ClaimReclaimOutcome.InvalidRequest(invalidReason);
        if (!TryParseWorkItemId(input.PrimaryScopeId, out var workItemId))
            return new ClaimReclaimOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        var holder = new ClaimHolderDescriptor(input.HolderIdentity, input.HolderDisplay);
        if (string.IsNullOrWhiteSpace(holder.Identity))
        {
            var resolved = await _holderResolver.ResolveAsync(ct).ConfigureAwait(false);
            if (!resolved.IsSuccess)
                return new ClaimReclaimOutcome.HolderUnavailable(resolved.Error);
            holder = resolved.Value;
        }

        // AllowSupersede=false: behaves exactly like a fresh mint. Any
        // pending/active row for the tuple refuses.
        if (!input.AllowSupersede)
        {
            var mintInput = new MintClaimInput(
                input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId,
                input.WorktreeFingerprint, holder.Identity, holder.DisplayName,
                input.Label, input.Notes, input.AdoProjection);
            var mintResult = await MintAsync(mintInput, ct).ConfigureAwait(false);
            return TranslateMintToReclaim(mintResult, supersededClaim: null);
        }

        // AllowSupersede=true. AB#737 §Reclaim over active.
        //
        // T1's partial unique index — "pending + active must be unique per
        // (connectionRef, workItemId)" — forbids inserting a second pending
        // row while the predecessor is still active. The design's step 1'
        // "insert pending row while predecessor lives" was written before
        // T1 nailed the constraint. Reconcile by skipping the pending
        // reservation entirely for the supersede path: the predecessor's
        // active state IS the tuple lock, so the ADO projection runs
        // straight against it; the atomic step 3' then supersedes the
        // predecessor AND inserts the new claim as active in one
        // transaction — the only order the partial index permits.
        var predecessorResult = await _registry.FindReservedClaimAsync(input.ConnectionRef, workItemId, ReservedStates, ct).ConfigureAwait(false);
        if (!predecessorResult.IsSuccess)
            return new ClaimReclaimOutcome.StorageUnavailable(predecessorResult.Error);
        var predecessorRow = predecessorResult.Value;
        if (predecessorRow is null)
            return new ClaimReclaimOutcome.ClaimNotActive("none");
        if (!string.Equals(predecessorRow.State, ClaimStates.Active, StringComparison.Ordinal))
        {
            // Refuse: reclaim never supersedes a still-pending row (AB#737
            // §Reclaim step 1').
            return new ClaimReclaimOutcome.ClaimNotActive(predecessorRow.State);
        }

        var predecessorRecord = TryDeserialize(predecessorRow, out var predecessorDoc, out var driftVersion);
        if (predecessorRecord is null)
            return new ClaimReclaimOutcome.SchemaDrift(driftVersion);
        var predecessorClaim = ProjectClaim(predecessorDoc!, predecessorRow.CasToken);

        // Step 2 — ADO holder projection. Runs against the still-active
        // predecessor. Failure leaves the DB untouched, so no mint-abort is
        // required — the predecessor stays active by construction.
        var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
        if (!projected.IsSuccess)
            return new ClaimReclaimOutcome.AdoProjectionFailed(projected.Error);

        // Step 3' — atomic supersession + insert-active. One storage
        // transaction guarantees that either the tuple flips fully
        // (predecessor superseded + new claim active) or neither write
        // lands. CAS mismatch on the predecessor surfaces
        // ConcurrentClaimWrite; a residual partial-index conflict (another
        // writer inserted a new active in between) surfaces
        // PrimaryScopeAlreadyClaimed with best-effort incumbent lookup.
        var now = _clock.GetUtcNow();
        var newClaimId = _idGen.NewClaimId();
        var newCas = _casGen.NewCasToken();
        var newCasPredecessor = _casGen.NewCasToken();

        var newActive = new ClaimRecord(
            SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
            ClaimId: newClaimId,
            Label: input.Label,
            ConnectionRef: input.ConnectionRef,
            PrimaryScopeId: input.PrimaryScopeId,
            PrimaryScopeKind: input.PrimaryScopeKind,
            HolderIdentity: holder.Identity,
            HolderDisplay: holder.DisplayName,
            WorktreeFingerprint: input.WorktreeFingerprint,
            State: ClaimStates.Active,
            Origin: ClaimOrigins.Local,
            LeaseGeneration: 0,
            ExpiresAt: null,
            CreatedAt: now,
            ActivatedAt: now,
            ReleasedAt: null,
            SupersededByClaimId: null,
            ReleaseReason: null,
            Notes: input.Notes,
            CasToken: newCas);
        var newJson = SerializeClaim(newActive);

        var supersededPredecessor = predecessorClaim with
        {
            State = ClaimStates.Superseded,
            ReleasedAt = now,
            ReleaseReason = ClaimReleaseReasons.ExplicitReclaim,
            SupersededByClaimId = newClaimId,
            CasToken = newCasPredecessor,
        };
        var predecessorJson = SerializeClaim(supersededPredecessor);

        var supersede = await _registry.SupersedeAndActivateClaimAsync(
            newClaimId: newClaimId,
            newCasToken: newCas,
            connectionRef: input.ConnectionRef,
            worktreeFingerprint: input.WorktreeFingerprint,
            workItemId: workItemId,
            newRecordJson: newJson,
            predecessorClaimId: predecessorClaim.ClaimId,
            predecessorExpectedCasToken: predecessorClaim.CasToken,
            predecessorNewCasToken: newCasPredecessor,
            predecessorRecordJson: predecessorJson,
            transitionAt: now,
            ct: ct).ConfigureAwait(false);
        if (!supersede.IsSuccess)
        {
            if (supersede.Error == AttachmentStorageFailure.ClaimCasMismatch)
                return new ClaimReclaimOutcome.ConcurrentClaimWrite(supersede.Error);
            if (supersede.Error.StartsWith(AttachmentStorageFailure.ClaimDuplicateReserved, StringComparison.Ordinal))
            {
                // Another writer raced. Best-effort incumbent lookup so
                // the caller sees the new holder.
                var incumbent = await _registry.FindReservedClaimAsync(input.ConnectionRef, workItemId, ReservedStates, ct).ConfigureAwait(false);
                if (incumbent.IsSuccess && incumbent.Value is { } incumbentRow)
                    return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed(incumbentRow.ClaimId, incumbentRow.State);
                return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed("unknown", "unknown");
            }
            return new ClaimReclaimOutcome.StorageUnavailable(supersede.Error);
        }

        // Step 4 — attachment linkage.
        var linked = await _attachment.LinkClaimAsync(newActive.ClaimId, newActive.ActivatedAt!.Value, ct).ConfigureAwait(false);
        if (!linked.IsSuccess)
            return new ClaimReclaimOutcome.AttachmentLinkFailed(linked.Error, newActive);
        return new ClaimReclaimOutcome.Succeeded(newActive, supersededPredecessor);
    }

    // ── Release ───────────────────────────────────────────────────────

    public async Task<ClaimReleaseOutcome> ReleaseAsync(ReleaseClaimInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ClaimId))
            return new ClaimReleaseOutcome.InvalidRequest("claimId is required.");
        if (input.AdoProjection is null)
            return new ClaimReleaseOutcome.InvalidRequest("adoProjection is required.");

        var rowResult = await _registry.FindClaimAsync(input.ClaimId, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess)
            return new ClaimReleaseOutcome.StorageUnavailable(rowResult.Error);
        if (rowResult.Value is null)
            return new ClaimReleaseOutcome.ClaimNotFound(input.ClaimId);
        var row = rowResult.Value;
        if (!string.Equals(row.State, ClaimStates.Active, StringComparison.Ordinal))
            return new ClaimReleaseOutcome.ClaimNotActive(input.ClaimId, row.State);

        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null)
            return new ClaimReleaseOutcome.SchemaDrift(driftVersion);
        var current = ProjectClaim(doc!, row.CasToken);

        // Step 1 — ADO clear. Local row remains active on failure.
        var cleared = await input.AdoProjection.ClearHolderAsync(current.PrimaryScopeId, ct).ConfigureAwait(false);
        if (!cleared.IsSuccess)
            return new ClaimReleaseOutcome.ReleaseAdoProjectionFailed(cleared.Error);

        // Step 2 — CAS-guarded local terminalize.
        var now = _clock.GetUtcNow();
        var newCas = _casGen.NewCasToken();
        var terminal = current with
        {
            State = ClaimStates.Released,
            ReleasedAt = now,
            ReleaseReason = ClaimReleaseReasons.ExplicitRelease,
            CasToken = newCas,
        };
        var json = SerializeClaim(terminal);
        var write = await _registry.UpdateClaimStateAsync(
            current.ClaimId, current.CasToken, newCas, ClaimStates.Released, now, json, ct).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            if (write.Error == AttachmentStorageFailure.ClaimCasMismatch)
                return new ClaimReleaseOutcome.ConcurrentClaimWrite(write.Error);
            return new ClaimReleaseOutcome.StorageUnavailable(write.Error);
        }

        // Step 3 — attachment unlink. Idempotent on the "already-unlinked"
        // path; a raw storage failure surfaces AttachmentUnlinkFailed with
        // the row already terminal so the caller re-runs unlink.
        var unlink = await _attachment.UnlinkClaimAsync(current.ClaimId, ct).ConfigureAwait(false);
        if (!unlink.IsSuccess)
            return new ClaimReleaseOutcome.AttachmentUnlinkFailed(unlink.Error, terminal);
        return new ClaimReleaseOutcome.Succeeded(terminal);
    }

    // ── Validate ──────────────────────────────────────────────────────

    public async Task<ClaimValidationOutcome> ValidateAsync(ClaimValidationInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ClaimId))
            return new ClaimValidationOutcome.InvalidRequest("claimId is required.");
        if (string.IsNullOrWhiteSpace(input.ConnectionRef))
            return new ClaimValidationOutcome.InvalidRequest("connectionRef is required.");
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeId))
            return new ClaimValidationOutcome.InvalidRequest("primaryScopeId is required.");
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeKind))
            return new ClaimValidationOutcome.InvalidRequest("primaryScopeKind is required.");

        var rowResult = await _registry.FindClaimAsync(input.ClaimId, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess)
            return new ClaimValidationOutcome.StorageUnavailable(rowResult.Error);
        if (rowResult.Value is null)
            return new ClaimValidationOutcome.ClaimNotFound(input.ClaimId);
        var row = rowResult.Value;
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null)
            return new ClaimValidationOutcome.SchemaDrift(driftVersion);
        var record = ProjectClaim(doc!, row.CasToken);
        if (!string.Equals(record.State, ClaimStates.Active, StringComparison.Ordinal))
            return new ClaimValidationOutcome.ClaimNotActive(record.ClaimId, record.State);

        // Byte-exact tuple check. AB#737 §Interface — TupleMismatch is
        // returned when the row exists but its tuple disagrees with the
        // caller-supplied tuple; this is a corruption signal.
        if (!string.Equals(record.ConnectionRef, input.ConnectionRef, StringComparison.Ordinal)
            || !string.Equals(record.PrimaryScopeKind, input.PrimaryScopeKind, StringComparison.Ordinal)
            || !string.Equals(record.PrimaryScopeId, input.PrimaryScopeId, StringComparison.Ordinal))
        {
            return new ClaimValidationOutcome.TupleMismatch(record);
        }
        return new ClaimValidationOutcome.Succeeded(record);
    }

    // ── LookupByTuple ─────────────────────────────────────────────────

    public async Task<ClaimLookupOutcome> LookupByTupleAsync(ClaimTupleQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.ConnectionRef)
            || string.IsNullOrWhiteSpace(query.PrimaryScopeKind)
            || string.IsNullOrWhiteSpace(query.PrimaryScopeId))
        {
            return new ClaimLookupOutcome.InvalidRequest("connectionRef, primaryScopeKind, primaryScopeId are required.");
        }
        if (!TryParseWorkItemId(query.PrimaryScopeId, out var workItemId))
            return new ClaimLookupOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        var rowResult = await _registry.FindReservedClaimAsync(query.ConnectionRef, workItemId, ReservedStates, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess)
            return new ClaimLookupOutcome.StorageUnavailable(rowResult.Error);
        var row = rowResult.Value;
        if (row is null)
            return new ClaimLookupOutcome.NotFound();
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null)
            return new ClaimLookupOutcome.SchemaDrift(driftVersion);
        return new ClaimLookupOutcome.Found(ProjectClaim(doc!, row.CasToken));
    }

    // ── UpdateLabel ───────────────────────────────────────────────────

    public async Task<ClaimLabelUpdateOutcome> UpdateLabelAsync(UpdateClaimLabelInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ClaimId))
            return new ClaimLabelUpdateOutcome.InvalidRequest("claimId is required.");
        if (string.IsNullOrWhiteSpace(input.ExpectedCasToken))
            return new ClaimLabelUpdateOutcome.InvalidRequest("expectedCasToken is required.");

        var rowResult = await _registry.FindClaimAsync(input.ClaimId, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess)
            return new ClaimLabelUpdateOutcome.StorageUnavailable(rowResult.Error);
        if (rowResult.Value is null)
            return new ClaimLabelUpdateOutcome.ClaimNotFound(input.ClaimId);
        var row = rowResult.Value;
        if (!string.Equals(row.CasToken, input.ExpectedCasToken, StringComparison.Ordinal))
            return new ClaimLabelUpdateOutcome.ConcurrentClaimWrite(AttachmentStorageFailure.ClaimCasMismatch);
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null)
            return new ClaimLabelUpdateOutcome.SchemaDrift(driftVersion);
        var current = ProjectClaim(doc!, row.CasToken);
        var newCas = _casGen.NewCasToken();
        var next = current with { Label = input.NewLabel, CasToken = newCas };
        var json = SerializeClaim(next);
        // Label update NEVER changes state; pass the current state through
        // so a lifecycle read observes an unchanged transition.
        var write = await _registry.UpdateClaimStateAsync(
            current.ClaimId, input.ExpectedCasToken, newCas, current.State, current.ReleasedAt, json, ct).ConfigureAwait(false);
        if (!write.IsSuccess)
        {
            if (write.Error == AttachmentStorageFailure.ClaimCasMismatch)
                return new ClaimLabelUpdateOutcome.ConcurrentClaimWrite(write.Error);
            return new ClaimLabelUpdateOutcome.StorageUnavailable(write.Error);
        }
        return new ClaimLabelUpdateOutcome.Succeeded(next);
    }

    // ── Internals ─────────────────────────────────────────────────────

    private readonly record struct ReservationResult(ClaimRecord? Claim, ClaimMintOutcome? Outcome);

    private async Task<ReservationResult> ReservePendingAsync(MintClaimInput input, ClaimHolderDescriptor holder, int workItemId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var newClaimId = _idGen.NewClaimId();
        var newCas = _casGen.NewCasToken();
        var pending = new ClaimRecord(
            SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
            ClaimId: newClaimId,
            Label: input.Label,
            ConnectionRef: input.ConnectionRef,
            PrimaryScopeId: input.PrimaryScopeId,
            PrimaryScopeKind: input.PrimaryScopeKind,
            HolderIdentity: holder.Identity,
            HolderDisplay: holder.DisplayName,
            WorktreeFingerprint: input.WorktreeFingerprint,
            State: ClaimStates.Pending,
            Origin: ClaimOrigins.Local,
            LeaseGeneration: 0,
            ExpiresAt: null,
            CreatedAt: now,
            ActivatedAt: null,
            ReleasedAt: null,
            SupersededByClaimId: null,
            ReleaseReason: null,
            Notes: input.Notes,
            CasToken: newCas);
        var json = SerializeClaim(pending);
        var insert = await _registry.InsertClaimAsync(
            pending.ClaimId, input.ConnectionRef, input.WorktreeFingerprint,
            workItemId, ClaimStates.Pending, newCas, json, ct).ConfigureAwait(false);
        if (insert.IsSuccess)
            return new ReservationResult(pending, null);

        // Uniqueness violation: attempt to resolve the incumbent so the
        // caller sees who holds it. The read is best-effort — a storage
        // hiccup here still surfaces PrimaryScopeAlreadyClaimed rather than
        // demoting to StorageUnavailable, because the insert already showed
        // the row is taken.
        if (insert.Error.StartsWith(AttachmentStorageFailure.ClaimDuplicateReserved, StringComparison.Ordinal))
        {
            var incumbent = await _registry.FindReservedClaimAsync(input.ConnectionRef, workItemId, ReservedStates, ct).ConfigureAwait(false);
            if (incumbent.IsSuccess && incumbent.Value is { } incumbentRow)
                return new ReservationResult(null, new ClaimMintOutcome.PrimaryScopeAlreadyClaimed(incumbentRow.ClaimId, incumbentRow.State));
            return new ReservationResult(null, new ClaimMintOutcome.PrimaryScopeAlreadyClaimed("unknown", "unknown"));
        }
        return new ReservationResult(null, new ClaimMintOutcome.StorageUnavailable(insert.Error));
    }

    private readonly record struct ActivationResult(ClaimRecord? Claim, ClaimMintOutcome? Outcome);

    private async Task<ActivationResult> ActivatePendingAsync(ClaimRecord pending, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var newCas = _casGen.NewCasToken();
        var active = pending with { State = ClaimStates.Active, ActivatedAt = now, CasToken = newCas };
        var json = SerializeClaim(active);
        var write = await _registry.UpdateClaimStateAsync(
            pending.ClaimId, pending.CasToken, newCas, ClaimStates.Active, null, json, ct).ConfigureAwait(false);
        if (write.IsSuccess)
            return new ActivationResult(active, null);
        if (write.Error == AttachmentStorageFailure.ClaimCasMismatch)
            return new ActivationResult(null, new ClaimMintOutcome.ConcurrentClaimWrite(write.Error));
        return new ActivationResult(null, new ClaimMintOutcome.StorageUnavailable(write.Error));
    }

    private async Task AbortPendingAsync(ClaimRecord pending, string reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var newCas = _casGen.NewCasToken();
        var aborted = pending with
        {
            State = ClaimStates.Released,
            ReleasedAt = now,
            ReleaseReason = reason,
            CasToken = newCas,
        };
        var json = SerializeClaim(aborted);
        // Fire-and-observe: the outer caller has already committed to the
        // AdoProjectionFailed outcome; a CAS failure here would only happen
        // if another writer terminalized the row first, which is safe. We
        // ignore the result — the row is already terminal in that case.
        _ = await _registry.UpdateClaimStateAsync(
            pending.ClaimId, pending.CasToken, newCas, ClaimStates.Released, now, json, ct).ConfigureAwait(false);
    }

    private static ClaimReclaimOutcome TranslateMintToReclaim(ClaimMintOutcome outcome, ClaimRecord? supersededClaim) => outcome switch
    {
        ClaimMintOutcome.Succeeded(var c) => new ClaimReclaimOutcome.Succeeded(c, supersededClaim),
        ClaimMintOutcome.PrimaryScopeAlreadyClaimed(var id, var state) => new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed(id, state),
        ClaimMintOutcome.AdoProjectionFailed(var u) => new ClaimReclaimOutcome.AdoProjectionFailed(u),
        ClaimMintOutcome.ConcurrentClaimWrite(var u) => new ClaimReclaimOutcome.ConcurrentClaimWrite(u),
        ClaimMintOutcome.AttachmentLinkFailed(var u, var c) => new ClaimReclaimOutcome.AttachmentLinkFailed(u, c),
        ClaimMintOutcome.HolderUnavailable(var u) => new ClaimReclaimOutcome.HolderUnavailable(u),
        ClaimMintOutcome.SchemaDrift(var v) => new ClaimReclaimOutcome.SchemaDrift(v),
        ClaimMintOutcome.StorageUnavailable(var u) => new ClaimReclaimOutcome.StorageUnavailable(u),
        ClaimMintOutcome.InvalidRequest(var r) => new ClaimReclaimOutcome.InvalidRequest(r),
        _ => new ClaimReclaimOutcome.StorageUnavailable("unknown mint outcome"),
    };

    private static bool ValidateMintInput(MintClaimInput input, out string reason)
    {
        if (string.IsNullOrWhiteSpace(input.ConnectionRef)) { reason = "connectionRef is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeKind)) { reason = "primaryScopeKind is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeId)) { reason = "primaryScopeId is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.WorktreeFingerprint)) { reason = "worktreeFingerprint is required."; return false; }
        if (input.AdoProjection is null) { reason = "adoProjection is required."; return false; }
        reason = string.Empty;
        return true;
    }

    private static bool ValidateReclaimInput(ReclaimClaimInput input, out string reason)
    {
        if (string.IsNullOrWhiteSpace(input.ConnectionRef)) { reason = "connectionRef is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeKind)) { reason = "primaryScopeKind is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeId)) { reason = "primaryScopeId is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.WorktreeFingerprint)) { reason = "worktreeFingerprint is required."; return false; }
        if (input.AdoProjection is null) { reason = "adoProjection is required."; return false; }
        reason = string.Empty;
        return true;
    }

    private static bool TryParseWorkItemId(string primaryScopeId, out int workItemId)
        => int.TryParse(primaryScopeId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0;

    /// <summary>
    /// Round-trips the opaque record_json through the source-generated
    /// context. Returns <c>null</c> and the observed <paramref name="schemaVersion"/>
    /// when the document rejects a higher version, uses unknown fields, or
    /// fails to parse — every one of which the caller maps to
    /// <c>SchemaDrift</c>. AB#737 §JSON encoding requires unknown fields on
    /// read be rejected as schema drift rather than ignored.
    /// </summary>
    private static ClaimRecordDocument? TryDeserialize(Twig.Domain.Interfaces.SystemClaimRow row, out ClaimRecordDocument? doc, out int schemaVersion)
    {
        doc = null; schemaVersion = 0;
        try
        {
            doc = JsonSerializer.Deserialize(row.RecordJson, TwigJsonContext.Default.ClaimRecordDocument);
        }
        catch (JsonException)
        {
            return null;
        }
        if (doc is null) return null;
        schemaVersion = doc.SchemaVersion;
        if (doc.SchemaVersion > ClaimRecordDocument.CurrentSchemaVersion) return null;
        if (doc.SchemaVersion < 1) return null;
        // Origin and state discriminators MUST match the version-1 vocabulary.
        if (!string.Equals(doc.Origin, ClaimOrigins.Local, StringComparison.Ordinal)) return null;
        if (doc.State is not (ClaimStates.Pending or ClaimStates.Active or ClaimStates.Released or ClaimStates.Superseded))
            return null;
        return doc;
    }

    private static ClaimRecord ProjectClaim(ClaimRecordDocument doc, string casTokenOverride)
    {
        DateTimeOffset created = ParseTimestamp(doc.CreatedAt);
        DateTimeOffset? activated = ParseNullableTimestamp(doc.ActivatedAt);
        DateTimeOffset? released = ParseNullableTimestamp(doc.ReleasedAt);
        DateTimeOffset? expires = ParseNullableTimestamp(doc.ExpiresAt);
        return new ClaimRecord(
            doc.SchemaVersion, doc.ClaimId, doc.Label, doc.ConnectionRef,
            doc.PrimaryScopeId, doc.PrimaryScopeKind, doc.HolderIdentity, doc.HolderDisplay,
            doc.WorktreeFingerprint, doc.State, doc.Origin, doc.LeaseGeneration,
            expires, created, activated, released, doc.SupersededByClaimId,
            doc.ReleaseReason, doc.Notes,
            // The row's cas_token column is the authoritative CAS token —
            // it is bumped even by writes that leave the record_json alone
            // (e.g. label updates through storage-only paths). Trust the
            // column over the JSON.
            casTokenOverride);
    }

    private static string SerializeClaim(ClaimRecord claim)
    {
        var doc = new ClaimRecordDocument(
            SchemaVersion: claim.SchemaVersion,
            ClaimId: claim.ClaimId,
            Label: claim.Label,
            ConnectionRef: claim.ConnectionRef,
            PrimaryScopeId: claim.PrimaryScopeId,
            PrimaryScopeKind: claim.PrimaryScopeKind,
            HolderIdentity: claim.HolderIdentity,
            HolderDisplay: claim.HolderDisplay,
            WorktreeFingerprint: claim.WorktreeFingerprint,
            State: claim.State,
            Origin: claim.Origin,
            LeaseGeneration: claim.LeaseGeneration,
            ExpiresAt: claim.ExpiresAt?.ToUniversalTime().ToString("o"),
            CreatedAt: claim.CreatedAt.ToUniversalTime().ToString("o"),
            ActivatedAt: claim.ActivatedAt?.ToUniversalTime().ToString("o"),
            ReleasedAt: claim.ReleasedAt?.ToUniversalTime().ToString("o"),
            SupersededByClaimId: claim.SupersededByClaimId,
            ReleaseReason: claim.ReleaseReason,
            Notes: claim.Notes,
            CasToken: claim.CasToken);
        return JsonSerializer.Serialize(doc, TwigJsonContext.Default.ClaimRecordDocument);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? ParseNullableTimestamp(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseTimestamp(value);
}
