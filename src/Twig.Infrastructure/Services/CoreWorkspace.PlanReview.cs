using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Policy;

namespace Twig.Infrastructure.Services;

internal sealed record CorePlanReviewCapture(CoreWorkspace Owner, NativeEditorMetadata Metadata, CapturedDeclaredPolicy Policy);
internal sealed record NativeVerifiedDraftReference(ItemKey Key, string Fingerprint);
internal sealed record CorePlanDeliveryCapture(NativeVerifiedDraftReference Draft, bool ConvergedInReview);
internal sealed record CoreVerifiedDeliveryTarget(ItemKey Key, bool PublishedSeed);
internal sealed record CorePlanDeliveryEvidence(ImmutableArray<CoreVerifiedDeliveryTarget> Targets,
    ImmutableHashSet<ItemKey> DeliveredKeys, bool AllEffectsVerified);

internal sealed partial class CoreWorkspace
{
    internal async Task<Result<CoreExecution>> ApplyFilePlanAsync(Plan plan, CancellationToken ct)
    {
        if (plan.Connection != connection || plan.Operations.Length != 1)
            return await ApplyAsync(plan, ct).ConfigureAwait(false);

        using var lease = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreExecution>.Fail(current.Error);
        var op = plan.Operations[0];
        var draft = current.Value.RetainedItems.SingleOrDefault(draft => MatchesFileIntent(draft, op));
        return draft is null ? await ApplyAsync(plan, ct).ConfigureAwait(false)
            : await SaveDraftAsync(new(draft, plan), ct).ConfigureAwait(false);
    }

    private static bool MatchesFileIntent(RetainedItem draft, Op op)
        => draft.Key == op.Item && draft.Fields.Equals(op.Fields) && (draft switch
        {
            Seed seed => op.Kind == OpKind.Create && seed.Type == op.Type && seed.Parent == op.Other
                && seed.ParentRelation == op.Relation && seed.Resource == op.Resource && seed.Links.SequenceEqual(op.Links),
            LocalEdits edits => op.Kind == OpKind.Batch && edits.EditBaseline.Revision == op.ExpectedRevision
                && op.Type == (edits.Type == edits.EditBaseline.Type ? null : edits.Type),
            _ => false,
        });

    internal async Task<Result<CorePlanReviewCapture>> CapturePlanReviewAsync(CancellationToken ct)
    {
        var metadata = await CaptureEditorMetadataAsync(ct).ConfigureAwait(false);
        if (!metadata.IsSuccess) return Result<CorePlanReviewCapture>.Fail(metadata.Error);
        var policy = await policies.CaptureAsync(connection, nativeActor, metadata.Value.Schema, ct).ConfigureAwait(false);
        return policy.IsSuccess ? Result<CorePlanReviewCapture>.Ok(new(this, metadata.Value, policy.Value))
            : Result<CorePlanReviewCapture>.Fail(policy.Error);
    }

    private bool OwnsPlanReview(CorePlanReviewCapture capture)
        => ReferenceEquals(capture.Owner, this) && capture.Metadata.Schema.Connection == connection
            && capture.Policy.Connection == connection && capture.Policy.ActorId == nativeActor
            && capture.Policy.NativeFingerprint == capture.Metadata.Schema.Fingerprint;

    internal async Task<Result<bool>> RevalidatePlanReviewPolicyAsync(CorePlanReviewCapture capture, CancellationToken ct)
    {
        if (!OwnsPlanReview(capture))
            return Result<bool>.Fail(ErrorCode.Conflict, "The operation-local review capture belongs to another exact scope.");
        var current = await policies.CaptureAsync(connection, nativeActor, capture.Metadata.Schema, ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        return current.Value.Document.Identity == capture.Policy.Document.Identity
            && current.Value.Document.Revision == capture.Policy.Document.Revision ? Result<bool>.Ok(true)
            : Result<bool>.Fail(ErrorCode.SchemaChanged, "Declared policy changed during this exact review operation.");
    }

    internal async Task<Result<bool>> SettleVerifiedDraftsAsync(Plan plan,
        ImmutableArray<NativeVerifiedDraftReference> captures, CancellationToken ct)
    {
        using var lease = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        var snapshot = current.Value;
        if (plan.Connection != connection || !snapshot.Plans.Any(value => value.Digest == plan.Digest))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "Exact original Verified Core receipts are required for local-only settlement.");
        if (captures.Select(capture => capture.Key).Distinct().Count() != captures.Length)
            return Result<bool>.Fail(ErrorCode.InvalidInput, "The original reviewed draft set must have unique exact identities.");
        foreach (var capture in captures)
        {
            var ordinal = plan.Operations.Select((op, index) => (op, index))
                .Where(value => value.op.Item == capture.Key).Select(value => (int?)value.index).SingleOrDefault();
            if (ordinal is null || !snapshot.Journal.Any(row =>
                row.Digest == plan.Digest && row.Operation == ordinal && row.Status == OpStatus.Verified))
                return Result<bool>.Fail(ErrorCode.Indeterminate, "Only attributable original Verified operations can settle local draft intent.");
            var draft = snapshot.RetainedItems.SingleOrDefault(value => value.Key == capture.Key);
            if (draft is not null && DraftFingerprint(draft) != capture.Fingerprint)
                return Result<bool>.Fail(ErrorCode.Conflict, "Remote effects verified; newer native draft intent was preserved.");
            if (capture.Key.IsLocal && !snapshot.Mappings.Any(mapping =>
                mapping.Local == capture.Key && mapping.PlanDigest == plan.Digest))
                return Result<bool>.Fail(ErrorCode.Indeterminate, "The original Verified seed mapping is required; its UUID was not recreated.");
        }
        var keys = captures.Select(capture => capture.Key).ToHashSet();
        if (!snapshot.RetainedItems.Any(draft => keys.Contains(draft.Key))) return Result<bool>.Ok(true);
        var settled = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, retainedItems: snapshot.RetainedItems.Where(draft => !keys.Contains(draft.Key))), ct).ConfigureAwait(false);
        return settled.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(settled.Error);
    }

    // Delivery may settle only original Verified effects or freshly confirmed no-effect captures.
    internal async Task<Result<CorePlanDeliveryEvidence>> RecoverPlanDraftsAsync(Plan? plan,
        ImmutableArray<CorePlanDeliveryCapture> captures, CancellationToken ct)
    {
        if (plan is not null && plan.Connection != connection)
            return Result<CorePlanDeliveryEvidence>.Fail(ErrorCode.Conflict, "The plan belongs to another immutable connection.");
        using var lease = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CorePlanDeliveryEvidence>.Fail(current.Error);
        var snapshot = current.Value;
        var verified = plan is null ? [] : VerifiedPlanOperations(snapshot, plan);
        var verifiedKeys = verified.Select(op => op.Item).ToImmutableHashSet();
        var delivered = captures.Where(capture => capture.ConvergedInReview || verifiedKeys.Contains(capture.Draft.Key)).ToImmutableArray();
        if (delivered.IsEmpty)
            return Result<CorePlanDeliveryEvidence>.Fail(ErrorCode.Indeterminate,
                "No original Verified effect or converged local settlement is available. Delivery never dispatches or guesses an outcome.");
        if (plan is not null && !verified.IsEmpty)
        {
            var settled = await SettleVerifiedDraftsAsync(plan, delivered.Select(capture => capture.Draft)
                .Where(draft => verifiedKeys.Contains(draft.Key)).ToImmutableArray(), ct).ConfigureAwait(false);
            if (!settled.IsSuccess) return Result<CorePlanDeliveryEvidence>.Fail(settled.Error);
        }
        var pending = ImmutableArray.CreateBuilder<CoreDraftPreview>();
        foreach (var capture in delivered.Where(capture => !verifiedKeys.Contains(capture.Draft.Key)))
        {
            var draft = snapshot.RetainedItems.SingleOrDefault(value => value.Key == capture.Draft.Key);
            if (draft is null) continue;
            if (DraftFingerprint(draft) != capture.Draft.Fingerprint)
                return Result<CorePlanDeliveryEvidence>.Fail(ErrorCode.Conflict, "Converged local settlement cannot consume changed Plan intent.");
            pending.Add(new(draft, null));
        }
        if (pending.Count != 0)
        {
            var settled = await SaveDraftSetAsync(pending, null, ct).ConfigureAwait(false);
            if (!settled.IsSuccess) return Result<CorePlanDeliveryEvidence>.Fail(settled.Error);
            if (settled.Value.DraftRetained || settled.Value.Error is not null)
                return Result<CorePlanDeliveryEvidence>.Fail(settled.Value.Error ?? new(ErrorCode.Conflict, "Local no-effect settlement remains pending."));
        }
        var targets = plan is null ? Result<ImmutableArray<CoreVerifiedDeliveryTarget>>.Ok([])
            : ResolveVerifiedDeliveryTargets(snapshot, plan.Digest, verified);
        return targets.IsSuccess ? Result<CorePlanDeliveryEvidence>.Ok(new(targets.Value,
            delivered.Select(capture => capture.Draft.Key).ToImmutableHashSet(), plan is null || verified.Length == plan.Operations.Length))
            : Result<CorePlanDeliveryEvidence>.Fail(targets.Error);
    }

    internal static ImmutableArray<Op> VerifiedPlanOperations(StoreSnapshot snapshot, Plan plan)
    {
        var digest = plan.Digest;
        if (!snapshot.Plans.Any(value => value.Digest == digest)) return [];
        var ordinals = snapshot.Journal.Where(row => row.Digest == digest && row.Status == OpStatus.Verified)
            .Select(row => row.Operation).ToHashSet();
        return plan.Operations.Where((_, ordinal) => ordinals.Contains(ordinal)).ToImmutableArray();
    }

    internal static Result<ImmutableArray<CoreVerifiedDeliveryTarget>> VerifiedDeliveryTargets(StoreSnapshot snapshot, Plan plan)
    {
        var verified = VerifiedPlanOperations(snapshot, plan);
        return verified.Length == plan.Operations.Length ? ResolveVerifiedDeliveryTargets(snapshot, plan.Digest, verified)
            : Result<ImmutableArray<CoreVerifiedDeliveryTarget>>.Fail(ErrorCode.Indeterminate,
                "Only original Verified journal operations can be delivered.");
    }

    private static Result<ImmutableArray<CoreVerifiedDeliveryTarget>> ResolveVerifiedDeliveryTargets(
        StoreSnapshot snapshot, string digest, ImmutableArray<Op> verified)
    {
        var targets = ImmutableArray.CreateBuilder<CoreVerifiedDeliveryTarget>();
        foreach (var op in verified)
        {
            if (!op.Item.IsLocal)
            {
                targets.Add(new(op.Item, false));
                continue;
            }
            var mapping = snapshot.Mappings.SingleOrDefault(value => value.Local == op.Item && value.PlanDigest == digest);
            if (mapping is null)
                return Result<ImmutableArray<CoreVerifiedDeliveryTarget>>.Fail(ErrorCode.Indeterminate,
                    "The attributable Verified seed mapping is unavailable.");
            targets.Add(new(mapping.Remote, true));
        }
        return Result<ImmutableArray<CoreVerifiedDeliveryTarget>>.Ok(targets.ToImmutable());
    }
}
