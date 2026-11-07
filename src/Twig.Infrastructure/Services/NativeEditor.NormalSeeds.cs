using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

public sealed record NativeSeedPublication(Guid CaptureId, ItemKey Item, string Digest, string Status, Error? Error);
public sealed record NativeOwnedSeed(ItemKey Item, Draft? Draft, Guid CaptureId, bool Ready, NativeSeedPublication? Publication);

internal sealed partial class NativeEditor
{
    private bool OwnsNormalSeed(NativeEditorCapture capture)
        => !plan.PlanMode && !capture.PlanMode && !capture.Cancelled && capture.ScopeId == Guid.Empty
            && capture.UserInstance == library.InstanceId && capture.BenchId == bench.Id
            && capture.Origin == origin && capture.StoreInstance == origin.StoreInstance
            && NativePathComparer.Instance.Equals(capture.Root, factory.WorkspaceRoot)
            && NativePathComparer.Instance.Equals(capture.ExecutionPath, handle.Document.DatabasePath)
            && UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single() is { Kind: OpKind.Create, Item.IsLocal: true };

    internal async Task<Result<ImmutableArray<NativeOwnedSeed>>> ReadOwnedSeedsAsync(CancellationToken ct)
    {
        if (plan.PlanMode || origin.Connection.Provider.Value != "ado")
            return Result<ImmutableArray<NativeOwnedSeed>>.Fail(ErrorCode.Unsupported, "Owned normal seed enumeration requires an exact normal ADO source.");
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<ImmutableArray<NativeOwnedSeed>>.Fail(valid.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<ImmutableArray<NativeOwnedSeed>>.Fail(captures.Error);
        var snapshot = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<ImmutableArray<NativeOwnedSeed>>.Fail(snapshot.Error);
        var seeds = ImmutableArray.CreateBuilder<NativeOwnedSeed>();
        foreach (var capture in captures.Value.Where(OwnsNormalSeed))
        {
            var key = UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Item;
            var draft = snapshot.Value.Drafts.SingleOrDefault(value => value.Key == key);
            var publication = capture.SeedProposal is null ? null : Publication(capture, snapshot.Value, null);
            if (capture.Settled && draft is null) continue;
            seeds.Add(new(key, draft, capture.Id, capture.Ready, publication));
        }
        return Result<ImmutableArray<NativeOwnedSeed>>.Ok(seeds.ToImmutable());
    }

    private async Task<Result<NativeEditorCapture>> SeedReferenceAsync(Guid captureId, CancellationToken ct)
    {
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<NativeEditorCapture>.Fail(captures.Error);
        var capture = captures.Value.SingleOrDefault(value => value.Id == captureId);
        return capture is not null && OwnsNormalSeed(capture) && capture.SeedProposal is not null
            ? Result<NativeEditorCapture>.Ok(capture)
            : Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "The exact original normal seed publication reference is unavailable or owned by another source/Bench.");
    }

    internal async Task<Result<NativeSeedPublication>> ObserveSeedAsync(Guid captureId, CancellationToken ct)
    {
        var reference = await SeedReferenceAsync(captureId, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<NativeSeedPublication>.Fail(reference.Error);
        var capture = reference.Value;
        var snapshot = await NativeCoreExecution.InspectAsync(new(capture.StoreInstance, capture.ExecutionPath,
            capture.Root, capture.Origin.Connection), ct).ConfigureAwait(false);
        return snapshot.IsSuccess ? Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, null))
            : Result<NativeSeedPublication>.Ok(new(capture.Id, UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Item,
                UserBenchStore.ParseIntent(capture.SeedProposal!).Digest, "Protected", snapshot.Error));
    }

    internal async Task<Result<NativeSeedPublication>> ContinueSeedDeliveryAsync(Guid captureId, string exactDigest, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var reference = await SeedReferenceAsync(captureId, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<NativeSeedPublication>.Fail(reference.Error);
        var capture = reference.Value;
        var proposal = UserBenchStore.ParseIntent(capture.SeedProposal!);
        if (proposal.Digest != exactDigest)
            return Result<NativeSeedPublication>.Fail(ErrorCode.DigestMismatch, "Continue delivery only for the exact original seed digest.");
        var snapshot = await NativeCoreExecution.InspectAsync(new(capture.StoreInstance, capture.ExecutionPath,
            capture.Root, capture.Origin.Connection), ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<NativeSeedPublication>.Fail(snapshot.Error);
        if (!NativeVerifiedDelivery.Verified(snapshot.Value, proposal))
            return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, new(ErrorCode.Indeterminate,
                "The original creation has no Verified journal outcome. Its UUID, proposal and receipts remain protected; delivery never dispatches.")));
        if (capture.Settled) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, null));
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, valid.Error));
        var settledDraft = await Workspace.SettleVerifiedDraftsAsync(proposal,
            [new(proposal.Operations.Single().Item, capture.DraftFingerprint)], ct).ConfigureAwait(false);
        if (!settledDraft.IsSuccess) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, settledDraft.Error));
        var pins = await NativeVerifiedDelivery.ReadbackAsync(snapshot.Value, proposal, proposal.Operations, Workspace, ct).ConfigureAwait(false);
        if (!pins.IsSuccess) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, pins.Error));
        valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, valid.Error));
        var pinned = await NativeVerifiedDelivery.AddPinsAsync(user, capture.UserInstance, capture.BenchId, pins.Value, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess) return Result<NativeSeedPublication>.Ok(Publication(capture, snapshot.Value, pinned.Error));
        var settledCapture = await NativeVerifiedDelivery.SettleCapturesAsync(user, capture.BenchId, pinned.Value.Revision,
            [capture], ct).ConfigureAwait(false);
        return Result<NativeSeedPublication>.Ok(Publication(capture with { Settled = settledCapture.IsSuccess },
            snapshot.Value, settledCapture.IsSuccess ? null : settledCapture.Error));
    }

    private static NativeSeedPublication Publication(NativeEditorCapture capture, StoreSnapshot snapshot, Error? error)
    {
        var proposal = UserBenchStore.ParseIntent(capture.SeedProposal!);
        var verified = NativeVerifiedDelivery.Verified(snapshot, proposal);
        if (!verified)
            error ??= snapshot.Journal.FirstOrDefault(row => row.Digest == proposal.Digest)?.Receipts.LastOrDefault()?.Error
                ?? new(ErrorCode.Indeterminate, "Original creation evidence is protected; no verified outcome was inferred.");
        return new(capture.Id, proposal.Operations.Single().Item, proposal.Digest,
            verified ? capture.Settled ? "Complete" : "VerifiedDeliveryPending" : "Protected", error);
    }
}
