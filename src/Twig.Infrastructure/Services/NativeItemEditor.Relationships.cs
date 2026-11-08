using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

public sealed record NativeRelationshipForm(NativeEditForm Fields, WorkItem Target, WorkItem Endpoint,
    ImmutableArray<RelationKind> Relations, ImmutableArray<CreationLink> Choices,
    ImmutableArray<CreationLink> Intent, ImmutableArray<Relationship> Incident);

public sealed partial class NativeItemEditor
{
    private readonly Dictionary<Guid, (WorkItem Item, NativeEditorEndpoint Proof)> relationshipEndpoints = [];

    public async Task<Result<NativeRelationshipForm>> SelectRelationshipsAsync(ItemKey key, ItemKey other, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (GitHub || PlanMode || key.IsLocal || other.IsLocal || key == other
                || !key.BelongsTo(editor.Origin.Connection) || !other.BelongsTo(editor.Origin.Connection))
                return Result<NativeRelationshipForm>.Fail(ErrorCode.Unsupported, "Qualified relationships require two explicit distinct published ADO endpoints in this normal source.");
            var valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
            if (!valid.IsSuccess) return Result<NativeRelationshipForm>.Fail(valid.Error);
            var schema = await editor.Workspace.SchemaAsync(ct).ConfigureAwait(false);
            if (!schema.IsSuccess) return Result<NativeRelationshipForm>.Fail(schema.Error);
            if (schema.Value.Fingerprint != editor.Metadata.Schema.Fingerprint)
                return Result<NativeRelationshipForm>.Fail(ErrorCode.SchemaChanged, "The captured relationship roster changed; reopen with fresh native metadata.");
            var endpoint = await editor.Workspace.ReadAsync(other, ct, editor.Metadata).ConfigureAwait(false);
            if (!endpoint.IsSuccess) return Result<NativeRelationshipForm>.Fail(endpoint.Error);
            var selected = await editor.SelectAsync(key, ct).ConfigureAwait(false);
            if (!selected.IsSuccess) return Result<NativeRelationshipForm>.Fail(selected.Error);
            var supported = SupportedItem(selected.Value);
            if (!supported.IsSuccess) return Result<NativeRelationshipForm>.Fail(supported.Error);
            var item = selected.Value;
            if (item.Capture is { } capture)
            {
                var op = UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single();
                if (!capture.Ready || capture.BenchId != editor.BenchId || capture.PlanMode || capture.SeedProposal is not null
                    || op.Links.Any(link => link.Other != other) || !op.Links.IsEmpty && (op.ExpectedRevision != item.Observation.Revision
                        || !capture.Endpoints.Any(proof => proof.Key == CleanCoreAdoAccess.ItemText(other) && proof.Revision == endpoint.Value.Revision?.Token)))
                    return Result<NativeRelationshipForm>.Fail(ErrorCode.Conflict, "The exact retained relationship capture, Bench or endpoint changed; no intent was rebound.");
            }
            if (items.Count >= 64) { items.Clear(); relationshipEndpoints.Clear(); }
            var ticket = Guid.NewGuid();
            var snapshot = await editor.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Result<NativeRelationshipForm>.Fail(snapshot.Error);
            if (snapshot.Value.Version != item.StoreVersion)
                return Result<NativeRelationshipForm>.Fail(ErrorCode.Conflict, "The endpoint draft evidence changed while opening the qualified ticket.");
            var proof = new NativeEditorEndpoint(CleanCoreAdoAccess.ItemText(other), endpoint.Value.Revision?.Token,
                editor.Workspace.DraftFingerprint(snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == other)));
            var form = await RelationshipFormAsync(ticket, item, endpoint.Value, ct).ConfigureAwait(false);
            if (!form.IsSuccess) return form;
            items[ticket] = item;
            relationshipEndpoints[ticket] = (endpoint.Value, proof);
            return form;
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeRelationshipForm>> StageRelationshipsAsync(Guid ticket, FieldValues changes,
        ImmutableArray<CreationLink> intent, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var staged = await StageRelationshipAsync(ticket, changes, intent, ct).ConfigureAwait(false);
            return staged.IsSuccess ? await RelationshipFormAsync(ticket, staged.Value, relationshipEndpoints[ticket].Item, ct).ConfigureAwait(false)
                : Result<NativeRelationshipForm>.Fail(staged.Error);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<NativeEditResult>> SaveRelationshipsAsync(Guid ticket, FieldValues changes,
        ImmutableArray<CreationLink> intent, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var staged = await StageRelationshipAsync(ticket, changes, intent, ct).ConfigureAwait(false);
            if (!staged.IsSuccess) return Result<NativeEditResult>.Fail(staged.Error);
            var saved = await editor.SaveAsync(staged.Value, ct).ConfigureAwait(false);
            if (!saved.IsSuccess) return Result<NativeEditResult>.Fail(saved.Error);
            if (saved.Value.EffectsVerified && !saved.Value.DraftRetained)
            {
                items.Remove(ticket);
                relationshipEndpoints.Remove(ticket);
            }
            return Result<NativeEditResult>.Ok(new(saved.Value.EffectsVerified, saved.Value.DraftRetained, saved.Value.Error));
        }
        finally { gate.Release(); }
    }

    private async Task<Result<NativeEditorItem>> StageRelationshipAsync(Guid ticket, FieldValues changes,
        ImmutableArray<CreationLink> intent, CancellationToken ct)
    {
        if (PlanMode || GitHub || !items.TryGetValue(ticket, out var item) || !relationshipEndpoints.TryGetValue(ticket, out var capturedEndpoint))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Reopen both explicit normal ADO endpoints with a qualified relationship ticket.");
        var endpoint = capturedEndpoint.Item;
        if (intent.IsDefault || intent.IsEmpty || intent.Length > 128 || intent.Any(link => link.Other != endpoint.Key))
            return Result<NativeEditorItem>.Fail(ErrorCode.InvalidInput, "Supply bounded explicit add/remove intent for this exact endpoint; empty intent cannot discard protected links.");
        var valid = await editor.ValidateAsync(ct).ConfigureAwait(false);
        if (!valid.IsSuccess) return Result<NativeEditorItem>.Fail(valid.Error);
        var evaluated = Form(ticket, item, changes);
        if (!evaluated.IsSuccess) return Result<NativeEditorItem>.Fail(evaluated.Error);
        if (!evaluated.Value.Evaluation.Errors.IsEmpty) return Result<NativeEditorItem>.Fail(evaluated.Value.Evaluation.Errors[0]);
        var current = await RelationshipFormAsync(ticket, item, endpoint, ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<NativeEditorItem>.Fail(current.Error);
        var retained = await editor.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!retained.IsSuccess) return Result<NativeEditorItem>.Fail(retained.Error);
        if (retained.Value.Journal.Any(row => row.Status is OpStatus.Applying or OpStatus.Applied or OpStatus.Partial or OpStatus.Indeterminate
            && retained.Value.Proposals.Single(proposal => proposal.Digest == row.Digest).Operations[row.Operation].Item == item.Observation.Key))
            return Result<NativeEditorItem>.Fail(ErrorCode.Indeterminate, "The original relationship attempt is protected; no restaging or replay is permitted.");
        var fresh = await editor.Workspace.ReadAsync(endpoint.Key, ct, editor.Metadata).ConfigureAwait(false);
        if (!fresh.IsSuccess) return Result<NativeEditorItem>.Fail(fresh.Error);
        if (fresh.Value.Revision != endpoint.Revision)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The captured relationship endpoint changed; reopen without rebinding retained intent.");
        var target = await editor.Workspace.ReadAsync(item.Observation.Key, ct, editor.Metadata).ConfigureAwait(false);
        if (!target.IsSuccess) return Result<NativeEditorItem>.Fail(target.Error);
        if (target.Value.Revision != item.Observation.Revision)
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "The captured relationship target changed; original intent was not restaged.");
        var prepared = await editor.Workspace.PrepareAsync([new("relationship", OpKind.Batch, item.Observation.Key,
            item.Observation.Revision, (item.Draft?.Changes ?? FieldValues.Empty).Overlay(changes),
            links: intent, concurrency: editor.Workspace.Concurrency)], ct).ConfigureAwait(false);
        if (!prepared.IsSuccess) return Result<NativeEditorItem>.Fail(prepared.Error);
        if (prepared.Value.SchemaFingerprint != editor.Metadata.Schema.Fingerprint)
            return Result<NativeEditorItem>.Fail(ErrorCode.SchemaChanged, "The relationship editor metadata changed; reopen the exact source.");
        var snapshot = await editor.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<NativeEditorItem>.Fail(snapshot.Error);
        if (editor.Workspace.DraftFingerprint(snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == item.Observation.Key))
            != editor.Workspace.DraftFingerprint(item.Draft))
            return Result<NativeEditorItem>.Fail(ErrorCode.Conflict, "Independent draft debt changed during relationship review.");
        var staged = await editor.StageAsync(item with { StoreVersion = snapshot.Value.Version }, changes, null, intent, ct,
            [capturedEndpoint.Proof]).ConfigureAwait(false);
        if (staged.IsSuccess) items[ticket] = staged.Value;
        return staged;
    }

    private async Task<Result<NativeRelationshipForm>> RelationshipFormAsync(Guid ticket, NativeEditorItem item, WorkItem endpoint, CancellationToken ct)
    {
        var snapshot = await editor.Workspace.SnapshotAsync(ct).ConfigureAwait(false);
        if (!snapshot.IsSuccess) return Result<NativeRelationshipForm>.Fail(snapshot.Error);
        if (snapshot.Value.Version != item.StoreVersion || item.Observation.Revision is null || endpoint.Revision is null
            || snapshot.Value.Items.SingleOrDefault(value => value.Key == item.Observation.Key)?.Revision != item.Observation.Revision
            || snapshot.Value.Items.SingleOrDefault(value => value.Key == endpoint.Key)?.Revision != endpoint.Revision)
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Conflict, "The coherent item or endpoint observation changed.");
        if (!item.Observation.RelationshipsComplete || !endpoint.RelationshipsComplete)
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Unavailable, "Complete incident evidence for both explicit endpoints is required.");
        if (relationshipEndpoints.TryGetValue(ticket, out var displayed)
            && (displayed.Proof.Revision != endpoint.Revision.Token
                || displayed.Proof.DraftFingerprint != editor.Workspace.DraftFingerprint(snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == endpoint.Key))))
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Conflict, "The qualified ticket's original endpoint draft evidence changed; no newer proof was adopted.");
        if (item.Capture is { } retained && !UserBenchStore.ParseIntent(retained.IntentJson).Operations.Single().Links.IsEmpty
            && !retained.Endpoints.Any(proof => proof.Key == CleanCoreAdoAccess.ItemText(endpoint.Key)
                && proof.Revision == endpoint.Revision.Token
                && proof.DraftFingerprint == editor.Workspace.DraftFingerprint(snapshot.Value.Drafts.SingleOrDefault(draft => draft.Key == endpoint.Key))))
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Conflict, "The original captured endpoint draft evidence changed; no debt was adopted.");
        var schema = editor.Metadata.Schema;
        var capabilities = editor.Workspace.Capabilities;
        var form = Form(ticket, item, FieldValues.Empty);
        if (!form.IsSuccess) return Result<NativeRelationshipForm>.Fail(form.Error);
        if (!form.Value.Evaluation.Errors.IsEmpty) return Result<NativeRelationshipForm>.Fail(form.Value.Evaluation.Errors[0]);
        if (!capabilities.AtomicFieldBatches || !capabilities.AtomicRelationshipBatches || !capabilities.ExactRevisions)
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Unsupported, "Atomic qualified relationship editing is unavailable; observations are read-only.");
        var relations = schema.Relations.Where(kind => kind.Shape != RelationShape.Resource
            && capabilities.NativeRelations.Contains(kind.Reference)).ToImmutableArray();
        if (relations.IsEmpty)
            return Result<NativeRelationshipForm>.Fail(ErrorCode.Unsupported, "No enabled canonical native incident relationship is qualified; no fallback is offered.");
        var choices = ImmutableArray.CreateBuilder<CreationLink>();
        foreach (var relation in relations)
            foreach (var outgoing in relation.Directed ? new[] { true, false } : [true])
                foreach (var remove in new[] { false, true })
                {
                    var link = new CreationLink(endpoint.Key, relation.Reference, outgoing, remove);
                    var candidate = new Proposal(editor.Origin.Connection, schema.Fingerprint, snapshot.Value.Version,
                        [new("choice", OpKind.Batch, item.Observation.Key, item.Observation.Revision, links: [link])]);
                    if (Model.Validate(candidate, candidate.Digest, schema, snapshot.Value, capabilities).IsEmpty) choices.Add(link);
                }
        var intent = item.Capture is { } capture ? UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Links : [];
        return Result<NativeRelationshipForm>.Ok(new(form.Value, item.Observation, endpoint, relations, choices.ToImmutable(), intent,
            snapshot.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider && !edge.Deleted
                && (edge.From == item.Observation.Key || edge.To == item.Observation.Key || edge.From == endpoint.Key || edge.To == endpoint.Key)).ToImmutableArray()));
    }
}
