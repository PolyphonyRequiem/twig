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
/// AB#739 lifecycle service. See <see cref="ILocalClaimService"/> for the
/// surface contract.
/// <para>
/// <b>Durable epoch protocol.</b> Every mint/reclaim/release runs against
/// a monotonic per-tuple <c>tuple_epochs</c> row in the system store:
/// <list type="number">
///   <item>Reserve the next epoch E before the remote ADO call.</item>
///   <item>ADO project (or clear) outside the SQLite transaction.</item>
///   <item>CAS-commit the lifecycle transition on the claim row.</item>
///   <item>CAS-commit the tuple epoch — <c>current_epoch = E</c> — writing
///     the winning claimId + CAS token that identifies who owns the
///     tuple at E. A later reserver would have raised the epoch and the
///     commit fails with <c>claim-tuple-epoch-mismatch</c>.</item>
///   <item>After a successful epoch commit, re-project (or re-clear) ADO
///     using the current authoritative winner from the epoch row and
///     verify the epoch has not moved. Bounded retry on change.</item>
/// </list>
/// This closes the "remote-write-before-future-commit" race: two racing
/// operations both write ADO, but only the one whose local commit lands
/// before another epoch reservation records itself as winner. The other
/// operation reads the epoch row and converges ADO to the recorded
/// winner.
/// </para>
/// <para>
/// Cancellation is phase-aware and never swallows cleanup failures — any
/// cleanup that itself fails throws <see cref="AggregateException"/>.
/// </para>
/// </summary>
internal sealed class LocalClaimService : ILocalClaimService
{
    private static readonly IReadOnlyList<string> ReservedStates = new[] { ClaimStates.Pending, ClaimStates.Active };
    private static readonly IReadOnlyList<string> ActiveOnlyStates = new[] { ClaimStates.Active };
    private const int ConvergenceMaxIterations = 8;

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

        var holderResult = await ResolveHolderAsync(input.HolderIdentity, input.HolderDisplay, ct).ConfigureAwait(false);
        if (!holderResult.IsSuccess)
            return new ClaimMintOutcome.HolderUnavailable(holderResult.Error);
        var holder = holderResult.Value;

        var attachmentRes = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachmentRes.IsSuccess)
            return new ClaimMintOutcome.AttachmentLinkFailed(attachmentRes.Error, PlaceholderRecord(input, holder));
        var attachedScope = attachmentRes.Value.Attachment.PrimaryScope;
        if (attachedScope is not { } scope || scope.WorkItemId != workItemId)
            return new ClaimMintOutcome.AttachmentLinkFailed(AttachmentStorageFailure.AttachmentScopeMismatch, PlaceholderRecord(input, holder));
        var expectedAttachmentRevision = attachmentRes.Value.Revision;

        var reservation = await ReservePendingAsync(input, holder, workItemId, ct).ConfigureAwait(false);
        if (reservation.Outcome is { } duplicateOrError)
            return duplicateOrError;
        var pending = reservation.Claim!;

        var phase = MintPhase.PreRemote;
        ClaimRecord? active = null;
        long reservedEpoch = 0;
        bool epochWasReserved = false;

        try
        {
            // Step 1' — reserve epoch BEFORE ADO write.
            var epochRes = await _registry.ReserveTupleEpochAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ct).ConfigureAwait(false);
            if (!epochRes.IsSuccess)
            {
                // Blocker (2): inspect the abort result explicitly. We
                // MUST NOT claim terminalization when abort itself failed;
                // fold both the epoch failure and the cleanup failure into
                // a named aggregate result the caller sees on the outcome.
                var abort = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, CancellationToken.None).ConfigureAwait(false);
                if (!abort.IsSuccess)
                {
                    if (abort.Error == AttachmentStorageFailure.ClaimCasMismatch)
                        return new ClaimMintOutcome.ConcurrentClaimWrite($"epoch-reserve-failed:{epochRes.Error}; pending-abort-cas-mismatch:{abort.Error}");
                    return new ClaimMintOutcome.StorageUnavailable($"epoch-reserve-failed:{epochRes.Error}; pending-abort-failed:{abort.Error}");
                }
                return new ClaimMintOutcome.StorageUnavailable($"epoch-reserve-failed:{epochRes.Error}");
            }
            reservedEpoch = epochRes.Value;
            epochWasReserved = true;

            phase = MintPhase.RemoteInFlight;
            var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
            if (!projected.IsSuccess)
            {
                phase = MintPhase.RemoteFailed;
                var abort = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, CancellationToken.None).ConfigureAwait(false);
                var conv = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                if (!abort.IsSuccess)
                {
                    if (abort.Error == AttachmentStorageFailure.ClaimCasMismatch)
                        return new ClaimMintOutcome.ConcurrentClaimWrite($"mint-abort-failed:{abort.Error}; projection={projected.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                    return new ClaimMintOutcome.StorageUnavailable($"mint-abort-failed:{abort.Error}; projection={projected.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                }
                if (!conv.IsSuccess)
                    return new ClaimMintOutcome.AdoProjectionFailed($"{projected.Error}; converge-failed:{conv.Error}");
                return new ClaimMintOutcome.AdoProjectionFailed(projected.Error);
            }

            phase = MintPhase.RemoteCommitted;

            // Step 3' + 4' — atomic activate + epoch commit in ONE
            // BEGIN IMMEDIATE transaction. Epoch race triggers full
            // rollback: the row never activates unless we won the
            // tuple at our reserved epoch.
            var activatedAt = _clock.GetUtcNow();
            var newCas = _casGen.NewCasToken();
            var activeCandidate = pending with { State = ClaimStates.Active, ActivatedAt = activatedAt, CasToken = newCas };
            var activeJson = SerializeClaim(activeCandidate);
            var atomic = await _registry.ActivateClaimAndCommitEpochAsync(
                pending.ClaimId, pending.CasToken, newCas, activatedAt, activeJson,
                input.ConnectionRef, input.PrimaryScopeKind, workItemId, reservedEpoch, ct).ConfigureAwait(false);
            if (!atomic.IsSuccess)
            {
                var conv = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                if (atomic.Error == AttachmentStorageFailure.ClaimCasMismatch)
                    return new ClaimMintOutcome.ConcurrentClaimWrite($"{atomic.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                if (atomic.Error == AttachmentStorageFailure.ClaimTupleEpochMismatch)
                    return new ClaimMintOutcome.ConcurrentClaimWrite($"{atomic.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                return new ClaimMintOutcome.StorageUnavailable($"{atomic.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
            }
            active = activeCandidate;
            // Post-commit ADO reconciliation: verify our winning ADO write
            // is still the current authoritative signal.
            var reconcile = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, ct).ConfigureAwait(false);
            if (!reconcile.IsSuccess)
                return new ClaimMintOutcome.AdoProjectionFailed($"post-commit-converge-failed:{reconcile.Error}");

            var linked = await _attachment.LinkClaimAsync(
                active.ClaimId, active.ActivatedAt!.Value,
                input.PrimaryScopeKind, workItemId, expectedAttachmentRevision, ct).ConfigureAwait(false);
            if (!linked.IsSuccess)
                return new ClaimMintOutcome.AttachmentLinkFailed(linked.Error, active);

            var verifyLive = await VerifyRowStillLiveAsync(active, ct).ConfigureAwait(false);
            if (!verifyLive.IsSuccess)
            {
                var undo = await _attachment.UnlinkClaimAsync(active.ClaimId, expectedAttachmentRevision + 1, CancellationToken.None).ConfigureAwait(false);
                var comp = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                var msg = $"{verifyLive.Error}; unlink={(undo.IsSuccess ? "ok" : undo.Error)}; converge={(comp.IsSuccess ? "ok" : comp.Error)}";
                return new ClaimMintOutcome.ConcurrentClaimWrite(msg);
            }
            return new ClaimMintOutcome.Succeeded(active);
        }
        catch (OperationCanceledException)
        {
            await HandleMintCancellationAsync(phase, pending, active, input, workItemId, epochWasReserved, reservedEpoch).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Phase-aware cancellation cleanup for mint. Blocker (2): even when
    /// pending abort succeeds, RemoteInFlight cancellation must ALSO
    /// converge from fresh tuple state, because the ADO write may already
    /// have landed and needs to be reconciled with the eventual winner.
    /// </summary>
    private async Task HandleMintCancellationAsync(MintPhase phase, ClaimRecord pending, ClaimRecord? active, MintClaimInput input, int workItemId, bool epochWasReserved, long reservedEpoch)
    {
        _ = epochWasReserved; _ = reservedEpoch;
        var ct = CancellationToken.None;
        Result cleanup;
        switch (phase)
        {
            case MintPhase.PreRemote:
                cleanup = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, ct).ConfigureAwait(false);
                break;
            case MintPhase.RemoteInFlight:
            case MintPhase.RemoteCommitted:
                {
                    var abort = await AbortPendingAsync(pending, ClaimReleaseReasons.MintAbort, ct).ConfigureAwait(false);
                    // Always converge, even when abort succeeds — the ADO
                    // write may have landed and needs reconciliation.
                    var conv = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, ct).ConfigureAwait(false);
                    if (abort.IsSuccess && conv.IsSuccess) cleanup = Result.Ok();
                    else if (abort.IsSuccess) cleanup = Result.Fail($"converge-failed:{conv.Error}");
                    else if (conv.IsSuccess) cleanup = Result.Fail($"abort-failed:{abort.Error}");
                    else cleanup = Result.Fail($"abort-failed:{abort.Error}; converge-failed:{conv.Error}");
                }
                break;
            case MintPhase.LocalCommitted:
                cleanup = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, ct).ConfigureAwait(false);
                break;
            default:
                cleanup = Result.Ok();
                break;
        }
        _ = active;
        if (!cleanup.IsSuccess)
        {
            throw new AggregateException(
                $"mint canceled and phase-{phase} cleanup failed: {cleanup.Error}",
                new OperationCanceledException(),
                new InvalidOperationException(cleanup.Error));
        }
    }

    private enum MintPhase { PreRemote, RemoteInFlight, RemoteCommitted, RemoteFailed, LocalCommitted }

    // ── Reclaim ───────────────────────────────────────────────────────

    public async Task<ClaimReclaimOutcome> ReclaimAsync(ReclaimClaimInput input, CancellationToken ct = default)
    {
        if (!ValidateReclaimInput(input, out var invalidReason))
            return new ClaimReclaimOutcome.InvalidRequest(invalidReason);
        if (!TryParseWorkItemId(input.PrimaryScopeId, out var workItemId))
            return new ClaimReclaimOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        var holderResult = await ResolveHolderAsync(input.HolderIdentity, input.HolderDisplay, ct).ConfigureAwait(false);
        if (!holderResult.IsSuccess)
            return new ClaimReclaimOutcome.HolderUnavailable(holderResult.Error);
        var holder = holderResult.Value;

        if (!input.AllowSupersede)
        {
            var mintInput = new MintClaimInput(
                input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId,
                input.WorktreeFingerprint, holder.Identity, holder.DisplayName,
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

        var attachmentRes = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
        if (!attachmentRes.IsSuccess)
            return new ClaimReclaimOutcome.AttachmentLinkFailed(attachmentRes.Error, predecessorClaim);
        var attachedScope = attachmentRes.Value.Attachment.PrimaryScope;
        if (attachedScope is not { } scope || scope.WorkItemId != workItemId)
            return new ClaimReclaimOutcome.AttachmentLinkFailed(AttachmentStorageFailure.AttachmentScopeMismatch, predecessorClaim);
        var expectedAttachmentRevision = attachmentRes.Value.Revision;

        var phase = ReclaimPhase.PreRemote;
        ClaimRecord? newActive = null;
        long reservedEpoch = 0;

        try
        {
            var epochRes = await _registry.ReserveTupleEpochAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ct).ConfigureAwait(false);
            if (!epochRes.IsSuccess)
                return new ClaimReclaimOutcome.StorageUnavailable($"epoch-reserve-failed:{epochRes.Error}");
            reservedEpoch = epochRes.Value;

            phase = ReclaimPhase.RemoteInFlight;
            var projected = await input.AdoProjection.ProjectHolderAsync(input.PrimaryScopeId, holder, ct).ConfigureAwait(false);
            if (!projected.IsSuccess)
            {
                var conv = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                return new ClaimReclaimOutcome.AdoProjectionFailed(conv.IsSuccess ? projected.Error : $"{projected.Error}; converge-failed:{conv.Error}");
            }

            phase = ReclaimPhase.RemoteCommitted;
            var now = _clock.GetUtcNow();
            var newClaimId = _idGen.NewClaimId();
            var newCas = _casGen.NewCasToken();
            var newCasPredecessor = _casGen.NewCasToken();

            newActive = new ClaimRecord(
                SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
                ClaimId: newClaimId, Label: input.Label,
                ConnectionRef: input.ConnectionRef,
                PrimaryScopeId: input.PrimaryScopeId, PrimaryScopeKind: input.PrimaryScopeKind,
                HolderIdentity: holder.Identity, HolderDisplay: holder.DisplayName,
                WorktreeFingerprint: input.WorktreeFingerprint,
                State: ClaimStates.Active, Origin: ClaimOrigins.Local, LeaseGeneration: 0,
                ExpiresAt: null, CreatedAt: now, ActivatedAt: now, ReleasedAt: null,
                SupersededByClaimId: null, ReleaseReason: null, Notes: input.Notes,
                CasToken: newCas);
            var newJson = SerializeClaim(newActive);

            var supersededPredecessor = predecessorClaim with
            {
                State = ClaimStates.Superseded, ReleasedAt = now,
                ReleaseReason = ClaimReleaseReasons.ExplicitReclaim,
                SupersededByClaimId = newClaimId, CasToken = newCasPredecessor,
            };
            var predecessorJson = SerializeClaim(supersededPredecessor);

            var supersede = await _registry.SupersedeAndActivateClaimAndCommitEpochAsync(
                newClaimId: newClaimId, newCasToken: newCas,
                connectionRef: input.ConnectionRef, worktreeFingerprint: input.WorktreeFingerprint,
                primaryScopeKind: input.PrimaryScopeKind, workItemId: workItemId,
                newRecordJson: newJson,
                predecessorClaimId: predecessorClaim.ClaimId,
                predecessorExpectedCasToken: predecessorClaim.CasToken,
                predecessorNewCasToken: newCasPredecessor,
                predecessorRecordJson: predecessorJson,
                transitionAt: now,
                expectedEpoch: reservedEpoch, ct: ct).ConfigureAwait(false);
            if (!supersede.IsSuccess)
            {
                var conv = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                if (supersede.Error == AttachmentStorageFailure.ClaimCasMismatch)
                    return new ClaimReclaimOutcome.ConcurrentClaimWrite(conv.IsSuccess ? supersede.Error : $"{supersede.Error}; converge-failed:{conv.Error}");
                if (supersede.Error == AttachmentStorageFailure.ClaimTupleEpochMismatch)
                    return new ClaimReclaimOutcome.ConcurrentClaimWrite($"{supersede.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                if (supersede.Error.StartsWith(AttachmentStorageFailure.ClaimDuplicateReserved, StringComparison.Ordinal))
                {
                    var incumbent = await _registry.FindReservedClaimAsync(input.ConnectionRef, input.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
                    if (incumbent.IsSuccess && incumbent.Value is { } incumbentRow)
                        return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed(incumbentRow.ClaimId, incumbentRow.State);
                    return new ClaimReclaimOutcome.PrimaryScopeAlreadyClaimed("unknown", "unknown");
                }
                return new ClaimReclaimOutcome.StorageUnavailable(supersede.Error);
            }

            phase = ReclaimPhase.LocalCommitted;
            var reconcile = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, ct).ConfigureAwait(false);
            if (!reconcile.IsSuccess)
                return new ClaimReclaimOutcome.AdoProjectionFailed($"post-commit-converge-failed:{reconcile.Error}");

            var linked = await _attachment.LinkClaimAsync(
                newActive.ClaimId, newActive.ActivatedAt!.Value,
                input.PrimaryScopeKind, workItemId, expectedAttachmentRevision, ct).ConfigureAwait(false);
            if (!linked.IsSuccess)
                return new ClaimReclaimOutcome.AttachmentLinkFailed(linked.Error, newActive);

            var verifyLive = await VerifyRowStillLiveAsync(newActive, ct).ConfigureAwait(false);
            if (!verifyLive.IsSuccess)
            {
                var undo = await _attachment.UnlinkClaimAsync(newActive.ClaimId, expectedAttachmentRevision + 1, CancellationToken.None).ConfigureAwait(false);
                var comp = await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                return new ClaimReclaimOutcome.ConcurrentClaimWrite($"{verifyLive.Error}; unlink={(undo.IsSuccess ? "ok" : undo.Error)}; converge={(comp.IsSuccess ? "ok" : comp.Error)}");
            }
            return new ClaimReclaimOutcome.Succeeded(newActive, supersededPredecessor);
        }
        catch (OperationCanceledException)
        {
            await HandleReclaimCancellationAsync(phase, predecessorClaim, newActive, input, workItemId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandleReclaimCancellationAsync(ReclaimPhase phase, ClaimRecord predecessor, ClaimRecord? newActive, ReclaimClaimInput input, int workItemId)
    {
        _ = predecessor; _ = newActive;
        var ct = CancellationToken.None;
        var cleanup = phase switch
        {
            ReclaimPhase.PreRemote => Result.Ok(),
            _ => await ConvergeToTupleWinnerAsync(input.ConnectionRef, input.PrimaryScopeKind, input.PrimaryScopeId, workItemId, input.AdoProjection, ct).ConfigureAwait(false),
        };
        if (!cleanup.IsSuccess)
        {
            throw new AggregateException(
                $"reclaim canceled and phase-{phase} cleanup failed: {cleanup.Error}",
                new OperationCanceledException(),
                new InvalidOperationException(cleanup.Error));
        }
    }

    private enum ReclaimPhase { PreRemote, RemoteInFlight, RemoteCommitted, LocalCommitted }

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

        var phase = ReleasePhase.PreRemote;
        try
        {
            var epochRes = await _registry.ReserveTupleEpochAsync(current.ConnectionRef, current.PrimaryScopeKind, TryParseInt(current.PrimaryScopeId), ct).ConfigureAwait(false);
            if (!epochRes.IsSuccess)
                return new ClaimReleaseOutcome.StorageUnavailable($"epoch-reserve-failed:{epochRes.Error}");
            var reservedEpoch = epochRes.Value;

            phase = ReleasePhase.RemoteInFlight;
            var cleared = await input.AdoProjection.ClearHolderAsync(current.PrimaryScopeId, ct).ConfigureAwait(false);
            if (!cleared.IsSuccess)
                return new ClaimReleaseOutcome.ReleaseAdoProjectionFailed(cleared.Error);

            phase = ReleasePhase.RemoteCommitted;
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
            var write = await _registry.TerminalizeClaimAndCommitEpochAsync(
                current.ClaimId, current.CasToken, newCas, now, json,
                current.ConnectionRef, current.PrimaryScopeKind, TryParseInt(current.PrimaryScopeId),
                reservedEpoch, ct).ConfigureAwait(false);
            if (!write.IsSuccess)
            {
                // Blocker (4): EVERY local terminalization failure after
                // the remote clear — CAS mismatch, epoch mismatch,
                // storage, schema, IO — converges from the fresh tuple.
                var conv = await ConvergeToTupleWinnerAsync(current.ConnectionRef, current.PrimaryScopeKind, current.PrimaryScopeId, TryParseInt(current.PrimaryScopeId), input.AdoProjection, CancellationToken.None).ConfigureAwait(false);
                if (write.Error == AttachmentStorageFailure.ClaimCasMismatch)
                    return new ClaimReleaseOutcome.ConcurrentClaimWrite(conv.IsSuccess ? write.Error : $"{write.Error}; converge-failed:{conv.Error}");
                if (write.Error == AttachmentStorageFailure.ClaimTupleEpochMismatch)
                    return new ClaimReleaseOutcome.ConcurrentClaimWrite($"{write.Error}; converge={(conv.IsSuccess ? "ok" : conv.Error)}");
                return new ClaimReleaseOutcome.StorageUnavailable(conv.IsSuccess ? write.Error : $"{write.Error}; converge-failed:{conv.Error}");
            }

            phase = ReleasePhase.LocalCommitted;
            var reconcile = await ConvergeToTupleWinnerAsync(current.ConnectionRef, current.PrimaryScopeKind, current.PrimaryScopeId, TryParseInt(current.PrimaryScopeId), input.AdoProjection, ct).ConfigureAwait(false);
            if (!reconcile.IsSuccess)
                return new ClaimReleaseOutcome.ReleaseAdoProjectionFailed($"post-commit-converge-failed:{reconcile.Error}");

            var unlink = await _attachment.UnlinkClaimAsync(current.ClaimId, expectedAttachmentRevision, ct).ConfigureAwait(false);
            if (!unlink.IsSuccess)
                return new ClaimReleaseOutcome.AttachmentUnlinkFailed(unlink.Error, terminal);
            return new ClaimReleaseOutcome.Succeeded(terminal);
        }
        catch (OperationCanceledException)
        {
            await HandleReleaseCancellationAsync(phase, current, input, expectedAttachmentRevision).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Blocker (5): release LocalCommitted cancellation retries conditional
    /// attachment unlink with a fresh revision and CancellationToken.None,
    /// and any cleanup failure surfaces as
    /// <see cref="AggregateException"/>.
    /// </summary>
    private async Task HandleReleaseCancellationAsync(ReleasePhase phase, ClaimRecord current, ReleaseClaimInput input, long observedAttachmentRevision)
    {
        var ct = CancellationToken.None;
        Result cleanup;
        switch (phase)
        {
            case ReleasePhase.PreRemote:
                cleanup = Result.Ok();
                break;
            case ReleasePhase.RemoteInFlight:
            case ReleasePhase.RemoteCommitted:
                cleanup = await ConvergeToTupleWinnerAsync(current.ConnectionRef, current.PrimaryScopeKind, current.PrimaryScopeId, TryParseInt(current.PrimaryScopeId), input.AdoProjection, ct).ConfigureAwait(false);
                break;
            case ReleasePhase.LocalCommitted:
                {
                    // Retry the unlink with a fresh revision reading; the
                    // observed revision may already be stale by cancellation.
                    var freshAttachment = await _attachment.ReadWithRevisionAsync(ct).ConfigureAwait(false);
                    var freshRevision = freshAttachment.IsSuccess ? freshAttachment.Value.Revision : observedAttachmentRevision;
                    var unlink = await _attachment.UnlinkClaimAsync(current.ClaimId, freshRevision, ct).ConfigureAwait(false);
                    cleanup = unlink.IsSuccess ? Result.Ok() : Result.Fail($"unlink-failed:{unlink.Error}");
                }
                break;
            default:
                cleanup = Result.Ok();
                break;
        }
        if (!cleanup.IsSuccess)
        {
            throw new AggregateException(
                $"release canceled and phase-{phase} cleanup failed: {cleanup.Error}",
                new OperationCanceledException(),
                new InvalidOperationException(cleanup.Error));
        }
    }

    private enum ReleasePhase { PreRemote, RemoteInFlight, RemoteCommitted, LocalCommitted }

    // ── Validate / Lookup / UpdateLabel (unchanged surface) ────────────

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
        if (!rowResult.IsSuccess) return new ClaimValidationOutcome.StorageUnavailable(rowResult.Error);
        if (rowResult.Value is null) return new ClaimValidationOutcome.ClaimNotFound(input.ClaimId);
        var row = rowResult.Value;
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null) return new ClaimValidationOutcome.SchemaDrift(driftVersion);
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

    public async Task<ClaimLookupOutcome> LookupByTupleAsync(ClaimTupleQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.ConnectionRef)
            || string.IsNullOrWhiteSpace(query.PrimaryScopeKind)
            || string.IsNullOrWhiteSpace(query.PrimaryScopeId))
            return new ClaimLookupOutcome.InvalidRequest("connectionRef, primaryScopeKind, primaryScopeId are required.");
        if (!TryParseWorkItemId(query.PrimaryScopeId, out var workItemId))
            return new ClaimLookupOutcome.InvalidRequest("primaryScopeId must be a positive integer.");

        var rowResult = await _registry.FindReservedClaimAsync(query.ConnectionRef, query.PrimaryScopeKind, workItemId, ReservedStates, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess) return new ClaimLookupOutcome.StorageUnavailable(rowResult.Error);
        var row = rowResult.Value;
        if (row is null) return new ClaimLookupOutcome.NotFound();
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null) return new ClaimLookupOutcome.SchemaDrift(driftVersion);
        return new ClaimLookupOutcome.Found(ProjectClaim(doc!, row.CasToken));
    }

    public async Task<ClaimLabelUpdateOutcome> UpdateLabelAsync(UpdateClaimLabelInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.ClaimId))
            return new ClaimLabelUpdateOutcome.InvalidRequest("claimId is required.");
        if (string.IsNullOrWhiteSpace(input.ExpectedCasToken))
            return new ClaimLabelUpdateOutcome.InvalidRequest("expectedCasToken is required.");

        var rowResult = await _registry.FindClaimAsync(input.ClaimId, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess) return new ClaimLabelUpdateOutcome.StorageUnavailable(rowResult.Error);
        if (rowResult.Value is null) return new ClaimLabelUpdateOutcome.ClaimNotFound(input.ClaimId);
        var row = rowResult.Value;
        if (!string.Equals(row.CasToken, input.ExpectedCasToken, StringComparison.Ordinal))
            return new ClaimLabelUpdateOutcome.ConcurrentClaimWrite(AttachmentStorageFailure.ClaimCasMismatch);
        var claim = TryDeserialize(row, out var doc, out var driftVersion);
        if (claim is null) return new ClaimLabelUpdateOutcome.SchemaDrift(driftVersion);
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

    private async Task<Result> VerifyRowStillLiveAsync(ClaimRecord active, CancellationToken ct)
    {
        var rowResult = await _registry.FindClaimAsync(active.ClaimId, ct).ConfigureAwait(false);
        if (!rowResult.IsSuccess) return Result.Fail($"post-link-row-read-failed:{rowResult.Error}");
        if (rowResult.Value is null) return Result.Fail("post-link-row-missing");
        var row = rowResult.Value;
        if (!string.Equals(row.State, ClaimStates.Active, StringComparison.Ordinal))
            return Result.Fail($"post-link-row-state:{row.State}");
        if (!string.Equals(row.CasToken, active.CasToken, StringComparison.Ordinal))
            return Result.Fail("post-link-row-cas-drift");
        return Result.Ok();
    }

    /// <summary>
    /// Durable-epoch convergence loop. Reads the authoritative tuple epoch
    /// and its winning claim; if a winner is recorded, project that
    /// holder to ADO; otherwise clear ADO. Re-read after the ADO write
    /// and require both the epoch AND the winner identity to still match.
    /// If either moved, iterate. Bounded by
    /// <see cref="ConvergenceMaxIterations"/>. Any registry read failure
    /// surfaces up — the loop NEVER writes ADO after a failed read.
    /// </summary>
    private async Task<Result> ConvergeToTupleWinnerAsync(
        string connectionRef, string primaryScopeKind, string primaryScopeId,
        int workItemId, IAdoClaimProjection ado, CancellationToken ct)
    {
        long lastObservedEpoch = -1;
        string? lastObservedWinner = null;
        for (var iter = 0; iter < ConvergenceMaxIterations; iter++)
        {
            var epochBefore = await _registry.GetTupleEpochAsync(connectionRef, primaryScopeKind, workItemId, ct).ConfigureAwait(false);
            if (!epochBefore.IsSuccess)
                return Result.Fail($"converge-epoch-read-failed:{epochBefore.Error}");

            Result adoOp;
            var winnerId = epochBefore.Value.WinningClaimId;
            if (!string.IsNullOrEmpty(winnerId))
            {
                var winnerRow = await _registry.FindClaimAsync(winnerId, ct).ConfigureAwait(false);
                if (!winnerRow.IsSuccess)
                    return Result.Fail($"converge-winner-read-failed:{winnerRow.Error}");
                if (winnerRow.Value is null
                    || !string.Equals(winnerRow.Value.State, ClaimStates.Active, StringComparison.Ordinal))
                {
                    // Winner row terminated between epoch commit and now
                    // — the tuple has no live authority. Clear ADO.
                    adoOp = await ado.ClearHolderAsync(primaryScopeId, ct).ConfigureAwait(false);
                }
                else
                {
                    var doc = TryDeserialize(winnerRow.Value, out var d, out var driftV);
                    if (doc is null)
                        return Result.Fail($"converge-winner-drift:{driftV}");
                    var winner = ProjectClaim(d!, winnerRow.Value.CasToken);
                    var holder = new ClaimHolderDescriptor(winner.HolderIdentity, winner.HolderDisplay);
                    adoOp = await ado.ProjectHolderAsync(primaryScopeId, holder, ct).ConfigureAwait(false);
                }
            }
            else
            {
                adoOp = await ado.ClearHolderAsync(primaryScopeId, ct).ConfigureAwait(false);
            }
            if (!adoOp.IsSuccess)
                return Result.Fail($"converge-ado-failed:{adoOp.Error}");

            var epochAfter = await _registry.GetTupleEpochAsync(connectionRef, primaryScopeKind, workItemId, ct).ConfigureAwait(false);
            if (!epochAfter.IsSuccess)
                return Result.Fail($"converge-epoch-verify-failed:{epochAfter.Error}");

            if (epochAfter.Value.Epoch == epochBefore.Value.Epoch
                && string.Equals(epochAfter.Value.WinningClaimId, epochBefore.Value.WinningClaimId, StringComparison.Ordinal))
            {
                return Result.Ok();
            }
            lastObservedEpoch = epochAfter.Value.Epoch;
            lastObservedWinner = epochAfter.Value.WinningClaimId;
        }
        return Result.Fail($"converge-not-stable:last-epoch={lastObservedEpoch},last-winner={lastObservedWinner ?? "<none>"}");
    }

    private async Task<Result<ClaimHolderDescriptor>> ResolveHolderAsync(string? callerIdentity, string? callerDisplay, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(callerIdentity))
            return Result.Ok(new ClaimHolderDescriptor(callerIdentity!, callerDisplay));
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
            ClaimId: newClaimId, Label: input.Label,
            ConnectionRef: input.ConnectionRef,
            PrimaryScopeId: input.PrimaryScopeId, PrimaryScopeKind: input.PrimaryScopeKind,
            HolderIdentity: holder.Identity, HolderDisplay: holder.DisplayName,
            WorktreeFingerprint: input.WorktreeFingerprint,
            State: ClaimStates.Pending, Origin: ClaimOrigins.Local, LeaseGeneration: 0,
            ExpiresAt: null, CreatedAt: now, ActivatedAt: null, ReleasedAt: null,
            SupersededByClaimId: null, ReleaseReason: null, Notes: input.Notes,
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
        if (write.IsSuccess) return new ActivationResult(active, null);
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
            State = ClaimStates.Released, ReleasedAt = now,
            ReleaseReason = reason, CasToken = newCas,
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
        reason = string.Empty; return true;
    }

    private static bool ValidateReclaimInput(ReclaimClaimInput input, out string reason)
    {
        if (string.IsNullOrWhiteSpace(input.ConnectionRef)) { reason = "connectionRef is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeKind)) { reason = "primaryScopeKind is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.PrimaryScopeId)) { reason = "primaryScopeId is required."; return false; }
        if (string.IsNullOrWhiteSpace(input.WorktreeFingerprint)) { reason = "worktreeFingerprint is required."; return false; }
        if (input.AdoProjection is null) { reason = "adoProjection is required."; return false; }
        reason = string.Empty; return true;
    }

    private static bool TryParseWorkItemId(string primaryScopeId, out int workItemId)
        => int.TryParse(primaryScopeId, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out workItemId) && workItemId > 0;

    private static ClaimRecordDocument? TryDeserialize(SystemClaimRow row, out ClaimRecordDocument? doc, out int schemaVersion)
    {
        doc = null; schemaVersion = 0;
        ClaimRecordDocument? parsed;
        try { parsed = JsonSerializer.Deserialize(row.RecordJson, TwigJsonContext.Default.ClaimRecordDocument); }
        catch (JsonException) { return null; }
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
        if (!string.Equals(parsed.WorktreeFingerprint, row.WorktreeFingerprint, StringComparison.Ordinal)) return false;
        if (!string.Equals(parsed.CasToken, row.CasToken, StringComparison.Ordinal)) return false;
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
            doc.PrimaryScopeId!, doc.PrimaryScopeKind!, doc.HolderIdentity!, doc.HolderDisplay,
            doc.WorktreeFingerprint!, doc.State!, doc.Origin!, doc.LeaseGeneration,
            expires, created, activated, released, doc.SupersededByClaimId,
            doc.ReleaseReason, doc.Notes, casTokenOverride);
    }

    private ClaimRecord PlaceholderRecord(MintClaimInput input, ClaimHolderDescriptor holder)
    {
        var now = _clock.GetUtcNow();
        return new ClaimRecord(
            SchemaVersion: ClaimRecordDocument.CurrentSchemaVersion,
            ClaimId: string.Empty, Label: input.Label,
            ConnectionRef: input.ConnectionRef,
            PrimaryScopeId: input.PrimaryScopeId, PrimaryScopeKind: input.PrimaryScopeKind,
            HolderIdentity: holder.Identity, HolderDisplay: holder.DisplayName,
            WorktreeFingerprint: input.WorktreeFingerprint,
            State: ClaimStates.Pending, Origin: ClaimOrigins.Local, LeaseGeneration: 0,
            ExpiresAt: null, CreatedAt: now, ActivatedAt: null, ReleasedAt: null,
            SupersededByClaimId: null, ReleaseReason: null, Notes: input.Notes,
            CasToken: string.Empty);
    }

    private static string SerializeClaim(ClaimRecord claim)
    {
        var doc = new ClaimRecordDocument(
            SchemaVersion: claim.SchemaVersion, ClaimId: claim.ClaimId, Label: claim.Label,
            ConnectionRef: claim.ConnectionRef,
            PrimaryScopeId: claim.PrimaryScopeId, PrimaryScopeKind: claim.PrimaryScopeKind,
            HolderIdentity: claim.HolderIdentity, HolderDisplay: claim.HolderDisplay,
            WorktreeFingerprint: claim.WorktreeFingerprint,
            State: claim.State, Origin: claim.Origin, LeaseGeneration: claim.LeaseGeneration,
            ExpiresAt: claim.ExpiresAt?.ToUniversalTime().ToString("o"),
            CreatedAt: claim.CreatedAt.ToUniversalTime().ToString("o"),
            ActivatedAt: claim.ActivatedAt?.ToUniversalTime().ToString("o"),
            ReleasedAt: claim.ReleasedAt?.ToUniversalTime().ToString("o"),
            SupersededByClaimId: claim.SupersededByClaimId,
            ReleaseReason: claim.ReleaseReason, Notes: claim.Notes,
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
