using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Policy;

namespace Twig.Infrastructure.Services;

internal sealed record CorePlanReviewCapture(CoreWorkspace Owner, NativeEditorMetadata Metadata, CapturedDeclaredPolicy Policy);
internal sealed record NativeVerifiedDraftReference(ItemKey Key, string Fingerprint);

internal sealed partial class CoreWorkspace
{
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

    internal async Task<Result<bool>> SettleVerifiedDraftsAsync(Proposal proposal,
        ImmutableArray<NativeVerifiedDraftReference> captures, CancellationToken ct)
    {
        using var lease = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        var snapshot = current.Value;
        if (proposal.Connection != connection || !snapshot.Proposals.Any(value => value.Digest == proposal.Digest))
            return Result<bool>.Fail(ErrorCode.Indeterminate, "Exact original Verified Core receipts are required for local-only settlement.");
        if (captures.Select(capture => capture.Key).Distinct().Count() != captures.Length)
            return Result<bool>.Fail(ErrorCode.InvalidInput, "The original reviewed draft set must have unique exact identities.");
        foreach (var capture in captures)
        {
            var ordinal = proposal.Operations.Select((op, index) => (op, index))
                .Where(value => value.op.Item == capture.Key).Select(value => (int?)value.index).SingleOrDefault();
            if (ordinal is null || !snapshot.Journal.Any(row =>
                row.Digest == proposal.Digest && row.Operation == ordinal && row.Status == OpStatus.Verified))
                return Result<bool>.Fail(ErrorCode.Indeterminate, "Only attributable original Verified operations can settle local draft intent.");
            var draft = snapshot.Drafts.SingleOrDefault(value => value.Key == capture.Key);
            if (draft is not null && DraftFingerprint(draft) != capture.Fingerprint)
                return Result<bool>.Fail(ErrorCode.Conflict, "Remote effects verified; newer native draft intent was preserved.");
            if (capture.Key.IsLocal && !snapshot.Mappings.Any(mapping =>
                mapping.Local == capture.Key && mapping.ProposalDigest == proposal.Digest))
                return Result<bool>.Fail(ErrorCode.Indeterminate, "The original Verified seed mapping is required; its UUID was not recreated.");
        }
        var keys = captures.Select(capture => capture.Key).ToHashSet();
        if (!snapshot.Drafts.Any(draft => keys.Contains(draft.Key))) return Result<bool>.Ok(true);
        var settled = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, drafts: snapshot.Drafts.Where(draft => !keys.Contains(draft.Key))), ct).ConfigureAwait(false);
        return settled.IsSuccess ? Result<bool>.Ok(true) : Result<bool>.Fail(settled.Error);
    }
}
