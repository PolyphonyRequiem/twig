using System.Collections.Immutable;
using System.Text;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

internal sealed record NativeLocalRelationshipSelection(NativeEditorItem Target, NativeEditorItem Endpoint,
    long StoreVersion, CoreLocalRelationship Local, ImmutableArray<NativeEditorEndpoint> Endpoints);

internal sealed partial class NativeEditor
{
    internal async Task<Result<ImmutableArray<NativeLocalRelationshipReview>>> ReadLocalRelationshipReviewsAsync(CancellationToken ct)
    {
        if (plan.PlanMode || origin.Connection.Provider.Value != "github")
            return Result<ImmutableArray<NativeLocalRelationshipReview>>.Fail(ErrorCode.Unsupported,
                "Original LOCAL reviews require the exact opened normal GitHub source.");
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<ImmutableArray<NativeLocalRelationshipReview>>.Fail(valid.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<ImmutableArray<NativeLocalRelationshipReview>>.Fail(captures.Error);
        var reviews = ImmutableArray.CreateBuilder<NativeLocalRelationshipReview>();
        foreach (var capture in captures.Value.Where(capture => capture.LocalRelationship is not null
            && capture.UserInstance == library.InstanceId && capture.BenchId == bench.Id && !capture.PlanMode
            && capture.StoreInstance == handle.Document.DatabaseInstance && capture.Origin == origin
            && NativePathComparer.Instance.Equals(capture.Root, factory.WorkspaceRoot)
            && NativePathComparer.Instance.Equals(capture.ExecutionPath, handle.Document.DatabasePath)))
        {
            var proposal = UserBenchStore.ParseIntent(capture.IntentJson);
            var review = await ReadOriginalLocalRelationshipReviewAsync(capture.Id, proposal.Digest, ct).ConfigureAwait(false);
            if (!review.IsSuccess) return Result<ImmutableArray<NativeLocalRelationshipReview>>.Fail(review.Error);
            reviews.Add(review.Value);
        }
        valid = await ValidateAsync(ct).ConfigureAwait(false);
        return valid.IsSuccess ? Result<ImmutableArray<NativeLocalRelationshipReview>>.Ok(reviews.ToImmutable())
            : Result<ImmutableArray<NativeLocalRelationshipReview>>.Fail(valid.Error);
    }

    internal async Task<Result<NativeLocalRelationshipReview>> ReadLocalRelationshipReviewAsync(Guid captureId, string exactDigest, CancellationToken ct)
    {
        if (captureId == Guid.Empty || string.IsNullOrWhiteSpace(exactDigest))
            return Result<NativeLocalRelationshipReview>.Fail(ErrorCode.InvalidInput, "Name the original LOCAL capture and its exact canonical digest.");
        if (plan.PlanMode || origin.Connection.Provider.Value != "github")
            return Result<NativeLocalRelationshipReview>.Fail(ErrorCode.Unsupported,
                "Original LOCAL reviews require the exact opened normal GitHub source.");
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(valid.Error);
        var review = await ReadOriginalLocalRelationshipReviewAsync(captureId, exactDigest, ct).ConfigureAwait(false);
        if (!review.IsSuccess) return review;
        valid = await ValidateAsync(ct).ConfigureAwait(false);
        return valid.IsSuccess ? review : Result<NativeLocalRelationshipReview>.Fail(valid.Error);
    }

    private async Task<Result<NativeLocalRelationshipReview>> ReadOriginalLocalRelationshipReviewAsync(Guid captureId, string exactDigest, CancellationToken ct)
    {
        var reference = await LocalReferenceAsync(captureId, exactDigest, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(reference.Error);
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(state.Error);
        var retained = NativeLocalRelationshipEvidence.CheckOriginalReference(reference.Value, state.Value);
        if (!retained.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(retained.Error);
        var outcome = await LocalOutcomeAsync(reference.Value, null, ct).ConfigureAwait(false);
        return outcome.IsSuccess
            ? Result<NativeLocalRelationshipReview>.Ok(new(reference.Value.Id, UserBenchStore.ParseIntent(reference.Value.IntentJson), outcome.Value))
            : Result<NativeLocalRelationshipReview>.Fail(outcome.Error);
    }

    private bool LocalCaptureOwnsEndpoint(NativeEditorCapture capture, ItemKey key)
        => capture.LocalRelationship is not null && !capture.Settled && !capture.Cancelled
            && capture.StoreInstance == handle.Document.DatabaseInstance
            && capture.Endpoints.Any(endpoint => endpoint.Key == CleanCoreAdoAccess.ItemText(key));

    internal async Task<Result<bool>> LocalEndpointAvailableAsync(ItemKey key, CancellationToken ct)
    {
        if (origin.Connection.Provider.Value != "github") return Result<bool>.Ok(true);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<bool>.Fail(captures.Error);
        var owner = captures.Value.FirstOrDefault(capture => LocalCaptureOwnsEndpoint(capture, key));
        return owner is null ? Result<bool>.Ok(true) : Result<bool>.Fail(new(ErrorCode.Conflict,
            "The original LOCAL relationship capture protects this endpoint; use its exact observation/delivery reference.")
        {
            Evidence = [$"capture:{owner.Id:D}", $"digest:{UserBenchStore.ParseIntent(owner.IntentJson).Digest}"],
        });
    }

    internal async Task<Result<NativeLocalRelationshipSelection>> SelectLocalRelationshipAsync(ItemKey key, ItemKey other, CancellationToken ct)
    {
        if (plan.PlanMode || origin.Connection.Provider.Value != "github" || key.IsLocal || other.IsLocal
            || key == other || !key.BelongsTo(origin.Connection) || !other.BelongsTo(origin.Connection))
            return Result<NativeLocalRelationshipSelection>.Fail(ErrorCode.Unsupported, "LOCAL qualification requires two explicit distinct published GitHub endpoints in the same normal source.");
        using var lease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var admitted = await ValidateAsync(ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(admitted.Error);
        var cached = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!cached.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(cached.Error);
        if (new[] { key, other }.Any(endpoint => !cached.Value.Items.Any(item => item.Key == endpoint
                && item.RelationshipsComplete && item.Revision is not null)
            || !CoreWorkspace.DisplayId(origin.Connection, cached.Value, endpoint).IsSuccess))
            return Result<NativeLocalRelationshipSelection>.Fail(ErrorCode.Unsupported, "Both published GitHub endpoints need complete qualified native observations before local pair selection; refresh them explicitly.");
        var target = await SelectAsync(key, ct).ConfigureAwait(false);
        if (!target.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(target.Error);
        var endpoint = await SelectAsync(other, ct).ConfigureAwait(false);
        if (!endpoint.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(endpoint.Error);
        if (target.Value.Draft is not null || endpoint.Value.Draft is not null
            || target.Value.Capture is not null || endpoint.Value.Capture is not null)
            return Result<NativeLocalRelationshipSelection>.Fail(ErrorCode.Conflict, "Independent scalar, Plan or captured debt is not adopted by a local pair.");
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(state.Error);
        var schema = await Workspace.SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(schema.Error);
        if (schema.Value.Fingerprint != metadata.Schema.Fingerprint)
            return Result<NativeLocalRelationshipSelection>.Fail(ErrorCode.SchemaChanged, "The captured local relationship schema changed; reopen the exact source.");
        var pair = CoreWorkspace.LocalRelationship(state.Value, schema.Value, Workspace.Capabilities, key, other);
        if (!pair.IsSuccess) return Result<NativeLocalRelationshipSelection>.Fail(pair.Error);
        if (state.Value.Journal.Any(row => row.Status is OpStatus.Applying or OpStatus.Applied or OpStatus.Partial or OpStatus.Indeterminate
            && state.Value.Proposals.Single(proposal => proposal.Digest == row.Digest).Operations[row.Operation] is { } prior
            && prior.Links.Select(link => link.Other).Prepend(prior.Item).Concat(prior.Other is null ? [] : new[] { prior.Other })
                .Any(endpoint => CoreWorkspace.LocalLogical(state.Value, endpoint) == key || CoreWorkspace.LocalLogical(state.Value, endpoint) == other)))
            return Result<NativeLocalRelationshipSelection>.Fail(ErrorCode.Indeterminate, "An original unresolved effect protects this local pair; no replacement review.");
        var endpoints = new[] { target.Value.Observation, endpoint.Value.Observation }.Select(item =>
            new NativeEditorEndpoint(CleanCoreAdoAccess.ItemText(item.Key), item.Revision?.Token,
                Workspace.DraftFingerprint(state.Value.Drafts.SingleOrDefault(draft => draft.Key == item.Key)))).ToImmutableArray();
        var selection = new NativeLocalRelationshipSelection(target.Value with { StoreVersion = state.Value.Version },
            endpoint.Value with { StoreVersion = state.Value.Version }, state.Value.Version, pair.Value, endpoints);
        var coherent = await ValidateLocalSelectionAsync(selection, ct).ConfigureAwait(false);
        return coherent.IsSuccess ? Result<NativeLocalRelationshipSelection>.Ok(selection)
            : Result<NativeLocalRelationshipSelection>.Fail(coherent.Error);
    }

    private async Task<Result<bool>> ValidateLocalSelectionAsync(NativeLocalRelationshipSelection selection, CancellationToken ct)
    {
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return valid;
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<bool>.Fail(state.Error);
        if (state.Value.Version != selection.StoreVersion)
            return Result<bool>.Fail(ErrorCode.Conflict, "The displayed local pair/store evidence changed; no recapture.");
        var read = await metadata.Read([selection.Target.Observation.Key, selection.Endpoint.Observation.Key], ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
        if (!read.Value.Complete || read.Value.Items.Length != 2
            || selection.Endpoints.Any(endpoint => !read.Value.Items.Any(item => item.RelationshipsComplete
                && CleanCoreAdoAccess.ItemText(item.Key) == endpoint.Key && item.Revision?.Token == endpoint.Revision)
                || Workspace.DraftFingerprint(state.Value.Drafts.SingleOrDefault(draft =>
                    CleanCoreAdoAccess.ItemText(draft.Key) == endpoint.Key)) != endpoint.DraftFingerprint))
            return Result<bool>.Fail(ErrorCode.Conflict, "The displayed local endpoint or scalar draft evidence changed; no recapture.");
        return await ValidateAsync(ct).ConfigureAwait(false);
    }

    internal async Task<Result<NativeLocalRelationshipReview>> ReviewLocalRelationshipAsync(
        NativeLocalRelationshipSelection selection, bool adding, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var valid = await ValidateLocalSelectionAsync(selection, ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(valid.Error);
        var proposal = await Workspace.PrepareLocalRelationshipAsync(selection.Local.Source, selection.Local.Target, adding, ct,
            selection.StoreVersion, metadata.Schema.Fingerprint).ConfigureAwait(false);
        if (!proposal.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(proposal.Error);
        valid = await ValidateLocalSelectionAsync(selection, ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(valid.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(captures.Error);
        if (captures.Value.Any(capture => !capture.Settled && !capture.Cancelled
            && capture.StoreInstance == handle.Document.DatabaseInstance
            && (capture.Endpoints.Any(endpoint => selection.Endpoints.Any(selected => selected.Key == endpoint.Key))
                || selection.Endpoints.Any(endpoint => endpoint.Key == CleanCoreAdoAccess.ItemText(
                    UserBenchStore.ParseIntent(capture.IntentJson).Operations[0].Item)))))
            return Result<NativeLocalRelationshipReview>.Fail(ErrorCode.Conflict, "Original native intent already owns this pair; no replacement review.");
        var capture = new NativeEditorCapture(Guid.NewGuid(), 1, library.InstanceId, bench.Id, bench.Revision,
            plan.Id, plan.Revision, false, Guid.Empty, origin, factory.WorkspaceRoot, handle.Document.DatabaseInstance,
            selection.StoreVersion, Encoding.UTF8.GetString(Model.ProposalBytes(proposal.Value)), "none", false)
        {
            Endpoints = selection.Endpoints, ExecutionPath = handle.Document.DatabasePath,
            LocalRelationship = new(CoreWorkspace.LocalEdgeFingerprint(origin.Connection, selection.Local.Edge)),
        };
        var reserved = await user.SaveEditorCaptureAsync(capture, 0, ct).ConfigureAwait(false);
        if (!reserved.IsSuccess) return Result<NativeLocalRelationshipReview>.Fail(reserved.Error);
        var ready = await ReadyLocalCaptureAsync(capture, ct).ConfigureAwait(false);
        return ready.IsSuccess ? Result<NativeLocalRelationshipReview>.Ok(new(capture.Id, proposal.Value))
            : Result<NativeLocalRelationshipReview>.Fail(ready.Error with
                { Evidence = ready.Error.Evidence.Add($"capture:{capture.Id:D}").Add($"digest:{proposal.Value.Digest}") });
    }

    private async Task<Result<NativeEditorCapture>> ReadyLocalCaptureAsync(NativeEditorCapture capture, CancellationToken ct)
    {
        var valid = await ValidateLocalApplyAsync(capture, ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorCapture>.Fail(valid.Error);
        var admitted = await user.AdmitCaptureStageAsync(capture, null, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<NativeEditorCapture>.Fail(admitted.Error);
        var retained = await Workspace.RetainReviewAsync(UserBenchStore.ParseIntent(capture.IntentJson), ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativeEditorCapture>.Fail(retained.Error);
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<NativeEditorCapture>.Fail(state.Error);
        admitted = await user.AdmitCaptureStageAsync(capture, state.Value.Version, ct).ConfigureAwait(false);
        if (!admitted.IsSuccess) return Result<NativeEditorCapture>.Fail(admitted.Error);
        return await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1,
            Ready = true, StoreVersion = state.Value.Version }, capture.Revision, ct).ConfigureAwait(false);
    }

    private async Task<Result<NativeEditorCapture>> LocalReferenceAsync(Guid captureId, string digest, CancellationToken ct)
    {
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorCapture>.Fail(valid.Error);
        var captures = await user.ReadEditorCapturesAsync(ct).ConfigureAwait(false);
        if (!captures.IsSuccess) return Result<NativeEditorCapture>.Fail(captures.Error);
        var capture = captures.Value.SingleOrDefault(value => value.Id == captureId);
        if (capture?.LocalRelationship is null || capture.Cancelled || plan.PlanMode || capture.PlanMode
            || capture.UserInstance != library.InstanceId || capture.BenchId != bench.Id || capture.BenchRevision != bench.Revision
            || capture.PlanId != plan.Id || capture.PlanRevision != plan.Revision || capture.Origin != origin
            || capture.StoreInstance != handle.Document.DatabaseInstance || capture.ScopeId != Guid.Empty
            || !NativePathComparer.Instance.Equals(capture.Root, factory.WorkspaceRoot)
            || !NativePathComparer.Instance.Equals(capture.ExecutionPath, handle.Document.DatabasePath))
            return Result<NativeEditorCapture>.Fail(ErrorCode.Conflict, "The original local capture belongs to another exact source/Bench/mode/store.");
        return UserBenchStore.ParseIntent(capture.IntentJson).Digest != digest
            ? Result<NativeEditorCapture>.Fail(ErrorCode.DigestMismatch, "Use the original local relationship's exact canonical digest.")
            : Result<NativeEditorCapture>.Ok(capture);
    }

    private async Task<Result<bool>> ValidateLocalApplyAsync(NativeEditorCapture capture, CancellationToken ct)
    {
        var proposal = UserBenchStore.ParseIntent(capture.IntentJson);
        var schema = await Workspace.SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<bool>.Fail(schema.Error);
        if (schema.Value.Fingerprint != proposal.SchemaFingerprint || schema.Value.Fingerprint != metadata.Schema.Fingerprint)
            return Result<bool>.Fail(ErrorCode.SchemaChanged, "The original local schema changed; no fallback.");
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<bool>.Fail(state.Error);
        var pair = CoreWorkspace.LocalRelationship(state.Value, schema.Value, Workspace.Capabilities,
            proposal.Operations[0].Item, proposal.Operations[0].Other!);
        if (!pair.IsSuccess) return Result<bool>.Fail(pair.Error);
        if (pair.Value.Kind.Reference != proposal.Operations[0].Relation)
            return Result<bool>.Fail(ErrorCode.Unsupported, "The original local-only Association is no longer declared.");
        var evidence = NativeLocalRelationshipEvidence.Check(capture, state.Value);
        if (!evidence.IsSuccess) return evidence;
        var keys = state.Value.Items.Where(item => capture.Endpoints.Any(endpoint =>
            endpoint.Key == CleanCoreAdoAccess.ItemText(item.Key))).Select(item => item.Key).ToImmutableArray();
        var fresh = await metadata.Read(keys, ct).ConfigureAwait(false);
        if (!fresh.IsSuccess) return Result<bool>.Fail(fresh.Error);
        if (!fresh.Value.Complete || fresh.Value.Items.Length != 2 || capture.Endpoints.Any(endpoint =>
            !fresh.Value.Items.Any(item => item.RelationshipsComplete && CleanCoreAdoAccess.ItemText(item.Key) == endpoint.Key
                && item.Revision?.Token == endpoint.Revision)))
            return Result<bool>.Fail(ErrorCode.Conflict, "The original captured local pair changed at the provider boundary.");
        return await ValidateAsync(ct).ConfigureAwait(false);
    }

    internal async Task<Result<NativeLocalRelationshipOutcome>> ApplyLocalRelationshipAsync(Guid captureId, string digest, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var reference = await LocalReferenceAsync(captureId, digest, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(reference.Error);
        var capture = reference.Value;
        var snapshot = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(snapshot.Error);
        var row = snapshot.Value.Journal.SingleOrDefault(value => value.Digest == digest && value.Operation == 0);
        if (!capture.Ready || capture.Settled || row?.Status != OpStatus.Planned)
            return Result<NativeLocalRelationshipOutcome>.Fail(ErrorCode.Indeterminate, "The original local attempt is protected; observe or continue verification/delivery only, never Apply again.");
        var valid = await ValidateLocalApplyAsync(capture, ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(valid.Error);
        var applied = await Workspace.ApplyAsync(UserBenchStore.ParseIntent(capture.IntentJson), digest, ct).ConfigureAwait(false);
        var result = await LocalOutcomeAsync(capture, applied.IsSuccess ? applied.Value.Error : applied.Error, ct).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value.Journal?.Status != OpStatus.Verified || result.Value.Error is not null) return result;
        return await SettleLocalCaptureAsync(capture, ct).ConfigureAwait(false);
    }

    internal async Task<Result<NativeLocalRelationshipOutcome>> ObserveLocalRelationshipAsync(Guid captureId, string digest, CancellationToken ct)
    {
        var reference = await LocalReferenceAsync(captureId, digest, ct).ConfigureAwait(false);
        return reference.IsSuccess ? await LocalOutcomeAsync(reference.Value, null, ct).ConfigureAwait(false)
            : Result<NativeLocalRelationshipOutcome>.Fail(reference.Error);
    }

    internal async Task<Result<NativeLocalRelationshipOutcome>> ContinueLocalRelationshipAsync(Guid captureId, string digest, CancellationToken ct)
    {
        using var benchLease = user.AcquireAuthoringLease(bench.Id);
        using var rootLease = ConnectionOperationGate.Acquire(factory.WorkspaceRoot, exclusive: true);
        var reference = await LocalReferenceAsync(captureId, digest, ct).ConfigureAwait(false);
        if (!reference.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(reference.Error);
        var capture = reference.Value;
        if (!capture.Ready)
        {
            var ready = await ReadyLocalCaptureAsync(capture, ct).ConfigureAwait(false);
            return ready.IsSuccess ? await LocalOutcomeAsync(ready.Value, null, ct).ConfigureAwait(false)
                : Result<NativeLocalRelationshipOutcome>.Fail(ready.Error);
        }
        var outcome = await LocalOutcomeAsync(capture, null, ct).ConfigureAwait(false);
        if (!outcome.IsSuccess || capture.Settled) return outcome;
        if (outcome.Value.Journal?.Status == OpStatus.Applied)
        {
            var valid = await ValidateLocalApplyAsync(capture, ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(valid.Error);
            var recovered = await Workspace.RecoverLocalRelationshipAsync(UserBenchStore.ParseIntent(capture.IntentJson), ct).ConfigureAwait(false);
            if (!recovered.IsSuccess) return await LocalOutcomeAsync(capture, recovered.Error, ct).ConfigureAwait(false);
            outcome = await LocalOutcomeAsync(capture, recovered.Value.Error, ct).ConfigureAwait(false);
        }
        return outcome.IsSuccess && outcome.Value.Journal?.Status == OpStatus.Verified && outcome.Value.Error is null
            ? await SettleLocalCaptureAsync(capture, ct).ConfigureAwait(false) : outcome;
    }

    private async Task<Result<NativeLocalRelationshipOutcome>> SettleLocalCaptureAsync(NativeEditorCapture capture, CancellationToken ct)
    {
        var valid = await ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return await LocalOutcomeAsync(capture, valid.Error, ct).ConfigureAwait(false);
        var settled = await user.SaveEditorCaptureAsync(capture with { Revision = capture.Revision + 1, Settled = true },
            capture.Revision, ct).ConfigureAwait(false);
        return await LocalOutcomeAsync(settled.IsSuccess ? settled.Value : capture, settled.IsSuccess ? null : settled.Error, ct).ConfigureAwait(false);
    }

    private async Task<Result<NativeLocalRelationshipOutcome>> LocalOutcomeAsync(NativeEditorCapture capture, Error? error, CancellationToken ct)
    {
        var state = await Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<NativeLocalRelationshipOutcome>.Fail(state.Error);
        var proposal = UserBenchStore.ParseIntent(capture.IntentJson);
        var row = state.Value.Journal.SingleOrDefault(value => value.Digest == proposal.Digest && value.Operation == 0);
        var proof = capture.Settled ? NativeLocalRelationshipEvidence.CheckSettledOutcome(capture, state.Value)
            : NativeLocalRelationshipEvidence.Check(capture, state.Value);
        error ??= !capture.Ready ? new(ErrorCode.Conflict, "The original capture reservation needs exact retention continuation; Apply is unavailable.")
            : row?.Status == OpStatus.Applying ? new(ErrorCode.Indeterminate, "The original attempt has no attributable durable outcome; no replay.")
            : null;
        return Result<NativeLocalRelationshipOutcome>.Ok(new(capture.Id, proposal.Digest, row, capture.Settled,
            error ?? (proof.IsSuccess ? row?.Receipts.LastOrDefault()?.Error : proof.Error)));
    }
}
