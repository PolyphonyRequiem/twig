using System.Text;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

internal static class NativeLocalRelationshipEvidence
{
    internal static bool IsLocal(Proposal proposal)
        => proposal.Connection.Provider.Value == "github" && proposal.Operations.Length == 1
            && proposal.Operations[0] is { Kind: OpKind.Relate or OpKind.Unrelate, Origin: RelationshipOrigin.Local,
                Other: not null, Relation: not null, ExpectedRevision: null, Type: null, Body: null, Resource: null } op
            && op.Fields.Count == 0 && op.Links.IsEmpty;

    internal static Result<bool> Check(NativeEditorCapture capture, StoreSnapshot state)
    {
        var proposal = UserBenchStore.ParseIntent(capture.IntentJson);
        if (!IsLocal(proposal) || capture.LocalRelationship is null || capture.PlanMode || capture.ScopeId != Guid.Empty
            || capture.StoreInstance != capture.Origin.StoreInstance || capture.SeedProposal is not null
            || capture.DraftFingerprint != "none" || capture.BeforeFingerprint != "none"
            || proposal.Connection != capture.Origin.Connection
            || Encoding.UTF8.GetString(Model.ProposalBytes(proposal)) != capture.IntentJson
            || capture.Endpoints.Length != 2 || capture.Endpoints.Select(endpoint => endpoint.Key).Distinct().Count() != 2)
            return Refuse("The exact single local-only capture shape or source changed.");
        var op = proposal.Operations[0];
        var from = CoreWorkspace.LocalLogical(state, op.Item);
        var to = CoreWorkspace.LocalLogical(state, op.Other!);
        if (from == to || from.IsLocal || to.IsLocal || !from.BelongsTo(proposal.Connection) || !to.BelongsTo(proposal.Connection)
            || !CoreWorkspace.DisplayId(proposal.Connection, state, from).IsSuccess
            || !CoreWorkspace.DisplayId(proposal.Connection, state, to).IsSuccess
            || new[] { from, to }.Any(key => !capture.Endpoints.Any(endpoint => endpoint.Key == CleanCoreAdoAccess.ItemText(key))))
            return Refuse("The original logical published pair or Verified UUID mapping is unavailable.");
        foreach (var endpoint in capture.Endpoints)
        {
            var item = state.Items.SingleOrDefault(item => CleanCoreAdoAccess.ItemText(item.Key) == endpoint.Key);
            if (item?.RelationshipsComplete != true || item.Revision is null || string.IsNullOrEmpty(endpoint.Revision)
                || item.Revision.Token != endpoint.Revision
                || CoreWorkspace.Fingerprint(proposal.Connection, state.Drafts.SingleOrDefault(draft => draft.Key == item.Key)) != endpoint.DraftFingerprint
                || endpoint.DraftFingerprint != "none")
                return Refuse("The exact local pair endpoint observation or independent draft evidence changed.");
        }
        var retained = state.Proposals.SingleOrDefault(value => value.Digest == proposal.Digest);
        var row = state.Journal.SingleOrDefault(value => value.Digest == proposal.Digest && value.Operation == 0);
        if (capture.Ready && (retained is null || row is null || state.Version < capture.StoreVersion)
            || retained is not null && Encoding.UTF8.GetString(Model.ProposalBytes(retained)) != capture.IntentJson
            || !capture.Ready && retained is null && state.Version != proposal.ExpectedStoreVersion)
            return Refuse("The original local proposal, journal or store-version evidence is unavailable.");
        if (row is null || row.Status == OpStatus.Planned)
        {
            if (capture.Ready && state.Version != capture.StoreVersion)
                return Refuse("Local store state moved after the original review; no rebase or replacement is permitted.");
        }
        var edges = state.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local
            && edge.Kind == op.Relation && CoreWorkspace.LocalTouches(state, edge, from, to)).ToArray();
        if (edges.Length > 1) return Refuse("Parallel logical local edge rows are not admissible.");
        var edge = edges.SingleOrDefault();
        var applied = row?.Receipts.Any(receipt => receipt.Status == OpStatus.Applied) == true;
        if (applied)
        {
            if (edge is null || edge.LocalVersion != (op.ExpectedLocalVersion ?? 0) + 1
                || edge.Deleted != (op.Kind == OpKind.Unrelate)
                || !((edge.From == op.Item && edge.To == op.Other) || op.ExpectedLocalVersion is null
                    && edge.From == op.Other && edge.To == op.Item)
                || row!.Receipts.Any(receipt => receipt.PublishedKey is not null || receipt.Revision is not null))
                return Refuse("The original local outcome edge/CAS or receipt differs; tracker publication was not inferred.");
        }
        else if (CoreWorkspace.LocalEdgeFingerprint(proposal.Connection, edge) != capture.LocalRelationship.EdgeFingerprint
            || op.ExpectedLocalVersion != edge?.LocalVersion
            || op.Kind == OpKind.Relate && edge is { Deleted: false }
            || op.Kind == OpKind.Unrelate && (edge is null || edge.Deleted))
            return Refuse("The original local edge/tombstone CAS moved; no replacement intent is permitted.");
        if (capture.Settled && row?.Status != OpStatus.Verified)
            return Refuse("A local capture can settle only its original Verified journal result.");
        return Result<bool>.Ok(true);
    }

    internal static Result<bool> CheckSettledOutcome(NativeEditorCapture capture, StoreSnapshot state)
    {
        var proposal = UserBenchStore.ParseIntent(capture.IntentJson);
        var retained = state.Proposals.SingleOrDefault(value => value.Digest == proposal.Digest);
        var row = state.Journal.SingleOrDefault(value => value.Digest == proposal.Digest && value.Operation == 0);
        return !capture.Settled || !IsLocal(proposal) || retained is null
            || Encoding.UTF8.GetString(Model.ProposalBytes(retained)) != capture.IntentJson
            || row is not { Status: OpStatus.Verified } || row.Attempt == Guid.Empty
            || row.Receipts.IsEmpty || row.Receipts[^1].Status != OpStatus.Verified
            || !row.Receipts.Any(receipt => receipt.Status == OpStatus.Applied)
            || row.Receipts.Any(receipt => receipt.Digest != proposal.Digest || receipt.Operation != 0
                || receipt.Attempt != row.Attempt || receipt.PublishedKey is not null || receipt.Revision is not null)
            ? Refuse("The original settled local journal/receipt attribution is unavailable; no Verified result was inferred.")
            : Result<bool>.Ok(true);
    }

    private static Result<bool> Refuse(string message) => Result<bool>.Fail(ErrorCode.Conflict, message);
}
