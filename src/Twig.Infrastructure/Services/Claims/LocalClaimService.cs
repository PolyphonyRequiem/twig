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
/// Concrete implementation of <see cref="ILocalClaimService"/>. See
/// <see cref="ILocalClaimService"/> for the surface contract.
/// <para>
/// <b>Cross-process lifecycle protocol (AB#739 review).</b> The service
/// does NOT rely on an in-process semaphore to serialize
/// mint/reclaim/release for a given tuple — an instance-local gate is
/// invisible to a peer process sharing the same system store and would
/// silently allow ADO and local authority to split. Instead the system
/// store's partial unique index + CAS tokens act as the tuple's
/// authority, and every path follows the same protocol:
/// <list type="number">
///   <item><b>Local intent commit.</b> Insert or CAS-transition the
///     claim row before the remote ADO call. The partial unique index
///     picks exactly one winner; losers get named outcomes without
///     touching ADO.</item>
///   <item><b>Remote projection, no SQLite txn held.</b> ADO project /
///     clear runs OUTSIDE the SQLite transaction so a network delay does
///     not hold the write lock. The projection re-fetches for readback
///     verification so a silent normalization surfaces as a named
///     failure.</item>
///   <item><b>CAS-guarded commit + compensation on loss.</b> The
///     activation / terminalization CAS conditions on the exact token
///     observed at step 1. On CAS mismatch (the tuple state moved
///     underneath us), the loser reads the fresh state and issues a
///     compensating ADO write — re-project the current active holder,
///     or clear ADO when no active row remains — so the ADO surface
///     always converges to the eventual winner's holder.</item>
///   <item><b>Attachment link/unlink with expected-revision CAS.</b>
///     The attachment store's OS-visible lock plus revision counter
///     provides cross-process CAS on the worktree-local block.</item>
/// </list>
/// A <see cref="OperationCanceledException"/> raised after step 1's
/// pending insert triggers a non-cancelable cleanup pass on a fresh
/// token so a canceled mint never strands a pending reservation.
/// </para>
/// </summary>
internal sealed class LocalClaimService : ILocalClaimService
{
    private static readonly IReadOnlyList<string> ReservedStates = new[] { ClaimStates.Pending, ClaimStates.Active };
    private static readonly IReadOnlyList<string> ActiveOnlyStates = new[] { ClaimStates.Active };

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

        var holderResult = await ResolveHolderAsync(input.HolderIdentity, input.HolderDisplay, input.HolderUniqueName, ct).ConfigureAwait(false);
        if (!holderResult.IsSuccess)
            return new ClaimMintOutcome.HolderUnavailable(holderResult.Error);
        var holder = holderResult.Value;

        // Read attachment (with expected revision) before we insert. The
        // scope must match the caller's tuple; the revision is threaded
        // into the eventual link call so a peer switch/unlink between
        // now and link surfaces as attachment-version-mismatch.
        var attachmentRes = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachmentRes.IsSuccess)
            return new ClaimMintOutcome.AttachmentLinkFailed(attachmentRes.Error, PlaceholderRecord(input, holder));
        var attachedScope = attachmentRes.Value.Attachment.PrimaryScope;
        if (attachedScope is not { } scope || scope.WorkItemId != workItemId)
            return new ClaimMintOutcome.AttachmentLinkFailed(AttachmentStorageFailure.AttachmentScopeMismatch, PlaceholderRecord(input, holder));
        var expectedAttachmentRevision = attachmentRes.Value.Revision;

        // Step 1 — reservation + insert. The partial unique index picks
        // the single winner across every process.
        var reservation = await ReservePendingAsync(input, holder, workItemId, ct).ConfigureAwait(false);
        if (reservation.Outcome is { } duplicateOrError)
            return duplicateOrError;
        var pending = reservation.Claim!;

        // From here on, an OperationCanceledException MUST run cleanup on
        // a fresh token so the pending row never lingers.
        try
        {
            // Step 2 — ADO projection outside SQLite tx.
            var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
            if (!projected.IsSuccess)
            {
                var abort = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, ct).ConfigureAwait(false);
                if (!abort.IsSuccess)
                {
                    if (abort.Error == AttachmentStorageFailure.ClaimCasMismatch)
                        return new ClaimMintOutcome.ConcurrentClaimWrite($"mint-abort-failed:{abort.Error}; projection={projected.Error}");
                    return new ClaimMintOutcome.StorageUnavailable($"mint-abort-failed:{abort.Error}; projection={projected.Error}");
                }
                return new ClaimMintOutcome.AdoProjectionFailed(projected.Error);
            }

            // Step 3 — activation under CAS. On CAS mismatch, run
            // compensation: read the current tuple state and re-project.
            var activation = await ActivatePendingAsync(pending, ct).ConfigureAwait(false);
            if (activation.Outcome is { } activationError)
            {
                if (activationError is ClaimMintOutcome.ConcurrentClaimWrite ccw)
                    await CompensateTupleAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                return activationError;
            }
            var active = activation.Claim!;

            // Step 4 — attachment link under expected-revision CAS.
            var linked = await _attachment.LinkClaimAsync(
                active.ClaimId, active.ActivatedAt!.Value,
                input.PrimaryScopeKind, workItemId, expectedAttachmentRevision, ct).ConfigureAwait(false);
            if (!linked.IsSuccess)
                return new ClaimMintOutcome.AttachmentLinkFailed(linked.Error, active);
            return new ClaimMintOutcome.Succeeded(active);
        }
        catch (OperationCanceledException)
        {
            // Non-cancelable cleanup: attempt to terminalize the pending
            // reservation and clear ADO. Failures are appended into the
            // exception's Data dictionary so a caller inspecting the
            // exception sees why cleanup stranded, rather than the row
            // silently persisting.
            var cleanup = await CleanupPendingAsync(pending, input.AdoProjection, input.PrimaryScopeId).ConfigureAwait(false);
            if (!cleanup.IsSuccess)
                throw new AggregateException(
                    $"mint canceled and cleanup failed: {cleanup.Error}",
                    new OperationCanceledException(),
                    new InvalidOperationException(cleanup.Error));
            throw;
        }
    }

    private async Task<Result> CleanupPendingAsync(ClaimRecord pending, IAdoClaimProjection ado, string primaryScopeId)
    {
        // Fresh CancellationToken.None so cleanup runs even after the
        // caller's token fired.
        var ct = CancellationToken.None;
        // Best-effort ADO clear — safe if ADO was never written to; the
        // clear path itself is idempotent (already-empty returns Ok).
        var clear = await ado.ClearHolderAsync(primaryScopeId, ct).ConfigureAwait(false);
        var abort = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, ct).ConfigureAwait(false);
        if (!abort.IsSuccess)
            return Result.Fail($"pending-terminalize-failed:{abort.Error}; ado-clear={(clear.IsSuccess ? "ok" : clear.Error)}");
        if (!clear.IsSuccess)
            return Result.Fail($"ado-clear-failed:{clear.Error}");
        return Result.Ok();
    }

    // ── Reclaim ───────────────────────────────────────────────────────

    public async Task<ClaimReclaimOutcome> ReclaimAsync(ReclaimClaimInput input, CancellationToken ct = default)
    {
        if (!ValidateReclaimInput(input, out var invalidReason))
            return new ClaimReclaimOutcome.InvalidRequest(invalidReason);
        if (!TryParseWorkItemId(input.PrimaryScopeId, out var workItemId))
            return new ClaimReclaimOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        var holderResult = await ResolveHolderAsync(input.HolderIdentity, input.HolderDisplay, input.HolderUniqueName, ct).ConfigureAwait(false);
        if (!holderResult.IsSuccess)
            return new ClaimReclaimOutcome.HolderUnavailable(holderResult.Error);
        var holder = holderResult.Value;

        if (!input.AllowSupersede)
        {
            var mintInput = new MintClaimInput(
                input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId,
                input.WorktreeFingerprint, holder.Identity, holder.DisplayName, holder.UniqueName,
                input.Label, input.Notes, input.AdoProjection);
            var mintResult = await MintAsync(mintInput, ct).ConfigureAwait(false);
            return TranslateMintToReclaim(mintResult, supersededClaim: null);
        }

        var predecessorResult = await _registry.FindReservedClaimAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
        if (!predecessorResult.IsSuccess)
            return new ClaimReclaimOutcome.StorageUnavailable(predecessorResult.Error);
        var predecessorRow = predecessorResult.Value;
        if (predecessorRow is null)
            return new ClaimReclaimOutcome.ClaimNotActive("none");
        if (!string.Equals(predecessorRow.State, ClaimStates.Active, StringComparison.Ordinal))
            return new ClaimReclaimOutcome.ClaimNotActive(predecessorRow.State);

        var predecessorRecord = TryDeserialize(predecessorRow, out var predecessorDoc, out var driftVersion);
        if (predecessorRecord is null)
            return new ClaimReclaimOutcome.SchemaDrift(driftVersion);
        var predecessorClaim = ProjectClaim(predecessorDoc!, predecessorRow.CasToken);

        // Read attachment with expected revision (scope-match precheck).
        var attachmentRes = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachmentRes.IsSuccess)
            return new ClaimReclaimOutcome.AttachmentLinkFailed(attachmentRes.Error, predecessorClaim);
        var attachedScope = attachmentRes.Value.Attachment.PrimaryScope;
        if (attachedScope is not { } scope || scope.WorkItemId != workItemId)
            return new ClaimReclaimOutcome.AttachmentLinkFailed(AttachmentStorageFailure.AttachmentScopeMismatch, predecessorClaim);
        var expectedAttachmentRevision = attachmentRes.Value.Revision;

        // Step 2 — ADO projection with our holder outside SQLite tx.
        var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
        if (!projected.IsSuccess)
            return new ClaimReclaimOutcome.AdoProjectionFailed(projected.Error);

        // Step 3' — atomic supersede + activate under CAS. Compensate on
        // CAS mismatch: another writer superseded us / released us; read
        // fresh state and re-project.
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
            HolderUniqueName: holder.UniqueName,
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
            primaryScopeKind: input.PrimaryScopeKind,
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
            // Compensate: our ADO write no longer represents the tuple
            // authority; re-project whoever won.
            await CompensateTupleAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
            if (supersede.Error == AttachmentStorageFailure.ClaimCasMismatch)
                return new ClaimReclaimOutcome.ConcurrentClaimWrite(supersede.Error);
            if (supersede.Error.StartsWith(AttachmentStorageFailure.ClaimDuplicateReserved, StringComparison.Ordinal))
            {
                var incumbent = await _registry.FindReservedClaimAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
                if (incumbent.IsSuccess && incumbent.Value is { } incumbentRow)
                    return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed(incumbentRow.ClaimId, incumbentRow.State);
                return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed("unknown", "unknown");
            }
            return new ClaimReclaimOutcome.StorageUnavailable(supersede.Error);
        }

        var linked = await _attachment.LinkClaimAsync(
            newActive.ClaimId, newActive.ActivatedAt!.Value,
            input.PrimaryScopeKind, workItemId, expectedAttachmentRevision, ct).ConfigureAwait(false);
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

        var attachmentRes = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachmentRes.IsSuccess)
            return new ClaimReleaseOutcome.AttachmentUnlinkFailed(attachmentRes.Error, current);
        var expectedAttachmentRevision = attachmentRes.Value.Revision;

        // Step 1 — ADO clear outside SQLite tx.
        var cleared = await input.AdoProjection.ClearHolderAsync(current.PrimaryScopeId, ct).ConfigureAwait(false);
        if (!cleared.IsSuccess)
            return new ClaimReleaseOutcome.ReleaseAdoProjectionFailed(cleared.Error);

        // Step 2 — CAS-guarded terminalize. Compensate on CAS mismatch:
        // a concurrent reclaim already superseded us and re-projected its
        // holder to ADO between our clear and our CAS — re-project that
        // successor rather than leaving ADO cleared.
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
            {
                await CompensateTupleAsync(current.ConnectionRef, current.PrimaryScopeKind, current.PrimaryScopeId, TryParseInt(current.PrimaryScopeId), input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                return new ClaimReleaseOutcome.ConcurrentClaimWrite(write.Error);
            }
            return new ClaimReleaseOutcome.StorageUnavailable(write.Error);
        }

        var unlink = await _attachment.UnlinkClaimAsync(current.ClaimId, expectedAttachmentRevision, ct).ConfigureAwait(false);
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

        var rowResult = await _registry.FindReservedClaimAsync(query.ConnectionRef, query.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Compensation for a lost CAS on release/reclaim: read the current
    /// state of the tuple and re-project (or clear) ADO to match. This
    /// is the readback half of the "conditional commit + compensation"
    /// protocol — the tuple's ADO surface converges to the winner's
    /// holder regardless of ordering.
    /// </summary>
    private async Task CompensateTupleAsync(
        string connectionRef, string primaryScopeKind, string primaryScopeId,
        int workItemId, IAdoClaimProjection ado, CancellationToken ct)
    {
        try
        {
            var reserved = await _registry.FindReservedClaimAsync(connectionRef, primaryScopeKind, workItemId, ActiveOnlyStates, ct).ConfigureAwait(false);
            if (reserved.IsSuccess && reserved.Value is { } activeRow)
            {
                var doc = TryDeserialize(activeRow, out var d, out _);
                if (doc is not null)
                {
                    var winner = ProjectClaim(d!, activeRow.CasToken);
                    var winnerHolder = new ClaimHolderDescriptor(
                        winner.HolderIdentity,
                        winner.HolderDisplay,
                        winner.HolderUniqueName);
                    await ado.ProjectHolderAsync(primaryScopeId, winnerHolder, ct).ConfigureAwait(false);
                    return;
                }
            }
            await ado.ClearHolderAsync(primaryScopeId, ct).ConfigureAwait(false);
        }
        catch
        {
            // Compensation is best-effort; a failure here does not
            // surface into the outcome (the CAS-mismatch outcome
            // already tells the caller their write did not land).
        }
    }

    private async Task<Result<ClaimHolderDescriptor>> ResolveHolderAsync(string? callerIdentity, string? callerDisplay, string? callerUniqueName, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(callerIdentity) && !string.IsNullOrWhiteSpace(callerUniqueName))
            return Result.Ok(new ClaimHolderDescriptor(callerIdentity!, callerDisplay, callerUniqueName));
        return await _holderResolver.ResolveAsync(ct).ConfigureAwait(false);
    }

    private static int TryParseInt(string value)
    {
        _ = int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v);
        return v;
    }

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
            HolderUniqueName: holder.UniqueName,
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
            input.PrimaryScopeKind, workItemId, ClaimStates.Pending, newCas, json, ct).ConfigureAwait(false);
        if (insert.IsSuccess)
            return new ReservationResult(pending, null);

        if (insert.Error.StartsWith(AttachmentStorageFailure.ClaimDuplicateReserved, StringComparison.Ordinal))
        {
            var incumbent = await _registry.FindReservedClaimAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
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

    private async Task<Result> AbortPendingAsync(ClaimRecord pending, string reason, CancellationToken ct)
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
        return await _registry.UpdateClaimStateAsync(
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

    private static ClaimRecordDocument? TryDeserialize(SystemClaimRow row, out ClaimRecordDocument? doc, out int schemaVersion)
    {
        doc = null; schemaVersion = 0;
        ClaimRecordDocument? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(row.RecordJson, TwigJsonContext.Default.ClaimRecordDocument);
        }
        catch (JsonException)
        {
            return null;
        }
        if (parsed is null) return null;
        schemaVersion = parsed.SchemaVersion;
        if (parsed.SchemaVersion != ClaimRecordDocument.CurrentSchemaVersion) return null;
        if (!ValidateRecordInvariants(parsed, row, out var validated)) return null;
        doc = validated;
        return validated;
    }

    private static bool ValidateRecordInvariants(ClaimRecordDocument parsed, SystemClaimRow row, out ClaimRecordDocument? doc)
    {
        doc = null;

        if (string.IsNullOrEmpty(parsed.ClaimId)) return false;
        if (string.IsNullOrEmpty(parsed.ConnectionRef)) return false;
        if (string.IsNullOrEmpty(parsed.PrimaryScopeId)) return false;
        if (string.IsNullOrEmpty(parsed.PrimaryScopeKind)) return false;
        if (string.IsNullOrEmpty(parsed.HolderIdentity)) return false;
        if (string.IsNullOrEmpty(parsed.WorktreeFingerprint)) return false;
        if (string.IsNullOrEmpty(parsed.State)) return false;
        if (string.IsNullOrEmpty(parsed.Origin)) return false;
        if (string.IsNullOrEmpty(parsed.CasToken)) return false;
        if (string.IsNullOrEmpty(parsed.CreatedAt)) return false;

        if (!string.Equals(parsed.Origin, ClaimOrigins.Local, StringComparison.Ordinal)) return false;
        if (parsed.State is not (ClaimStates.Pending or ClaimStates.Active or ClaimStates.Released or ClaimStates.Superseded))
            return false;

        if (parsed.LeaseGeneration != 0) return false;
        if (!string.IsNullOrEmpty(parsed.ExpiresAt)) return false;

        if (!TryParseTimestamp(parsed.CreatedAt!, out var created)) return false;
        DateTimeOffset? activated = null;
        DateTimeOffset? released = null;
        if (!string.IsNullOrEmpty(parsed.ActivatedAt))
        {
            if (!TryParseTimestamp(parsed.ActivatedAt!, out var act)) return false;
            activated = act;
        }
        if (!string.IsNullOrEmpty(parsed.ReleasedAt))
        {
            if (!TryParseTimestamp(parsed.ReleasedAt!, out var rel)) return false;
            released = rel;
        }

        switch (parsed.State)
        {
            case ClaimStates.Pending:
                if (activated.HasValue || released.HasValue) return false;
                if (parsed.ReleaseReason is not null) return false;
                if (parsed.SupersededByClaimId is not null) return false;
                break;
            case ClaimStates.Active:
                if (!activated.HasValue) return false;
                if (released.HasValue) return false;
                if (parsed.ReleaseReason is not null) return false;
                if (parsed.SupersededByClaimId is not null) return false;
                if (activated.Value < created) return false;
                break;
            case ClaimStates.Released:
                if (!released.HasValue) return false;
                if (parsed.ReleaseReason is null) return false;
                if (activated.HasValue && activated.Value < created) return false;
                if (released.Value < created) return false;
                if (parsed.SupersededByClaimId is not null) return false;
                break;
            case ClaimStates.Superseded:
                if (!activated.HasValue || !released.HasValue) return false;
                if (parsed.ReleaseReason is null) return false;
                if (string.IsNullOrEmpty(parsed.SupersededByClaimId)) return false;
                if (activated.Value < created) return false;
                if (released.Value < activated.Value) return false;
                break;
        }

        if (parsed.ReleaseReason is not null
            && parsed.ReleaseReason is not (ClaimReleaseReasons.ExplicitRelease or ClaimReleaseReasons.ExplicitReclaim or ClaimReleaseReasons.MintAbort))
            return false;

        if (!string.Equals(parsed.ClaimId, row.ClaimId, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.ConnectionRef, row.ConnectionRef, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.State, row.State, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.PrimaryScopeKind, row.PrimaryScopeKind, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parsed.PrimaryScopeId, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedWi) || parsedWi != row.WorkItemId) return false;

        doc = parsed;
        return true;
    }

    private static ClaimRecord ProjectClaim(ClaimRecordDocument doc, string casTokenOverride)
    {
        DateTimeOffset created = ParseTimestamp(doc.CreatedAt!);
        DateTimeOffset? activated = ParseNullableTimestamp(doc.ActivatedAt);
        DateTimeOffset? released = ParseNullableTimestamp(doc.ReleasedAt);
        DateTimeOffset? expires = ParseNullableTimestamp(doc.ExpiresAt);
        return new ClaimRecord(
            doc.SchemaVersion, doc.ClaimId!, doc.Label, doc.ConnectionRef!,
            doc.PrimaryScopeId!, doc.PrimaryScopeKind!, doc.HolderIdentity!, doc.HolderDisplay, doc.HolderUniqueName,
            doc.WorktreeFingerprint!, doc.State!, doc.Origin!, doc.LeaseGeneration,
            expires, created, activated, released, doc.SupersededByClaimId,
            doc.ReleaseReason, doc.Notes,
            casTokenOverride);
    }

    private ClaimRecord PlaceholderRecord(MintClaimInput input, ClaimHolderDescriptor holder)
    {
        var now = _clock.GetUtcNow();
        return new ClaimRecord(
            SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
            ClaimId: string.Empty,
            Label: input.Label,
            ConnectionRef: input.ConnectionRef,
            PrimaryScopeId: input.PrimaryScopeId,
            PrimaryScopeKind: input.PrimaryScopeKind,
            HolderIdentity: holder.Identity,
            HolderDisplay: holder.DisplayName,
            HolderUniqueName: holder.UniqueName,
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
            CasToken: string.Empty);
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
            HolderUniqueName: claim.HolderUniqueName,
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

    private static bool TryParseTimestamp(string value, out DateTimeOffset stamp) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out stamp);

    private static DateTimeOffset? ParseNullableTimestamp(string? value) =>
        string.IsNullOrEmpty(value) ? null : ParseTimestamp(value);
}
