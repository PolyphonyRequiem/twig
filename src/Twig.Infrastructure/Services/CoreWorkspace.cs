using System.Collections.Immutable;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Policy;
using Model = Twig.Core.Core;

namespace Twig.Infrastructure.Services;

internal sealed record CoreDraftPreview(Draft Draft, Proposal? Proposal);
internal sealed record CoreExecution(ImmutableArray<Receipt> Receipts, Error? Error = null,
    bool AlreadyVerified = false, bool DraftRetained = false)
{
    internal bool EffectsVerified => AlreadyVerified || !Receipts.IsEmpty && Receipts.All(r => r.Status == OpStatus.Verified);
}

/// <summary>One draft/proposal/journal application path; no retry, note flush or legacy publisher.</summary>
internal sealed class CoreWorkspace(Connection connection, IProvider provider, IStore store,
    ConfiguredPolicyProjectionSource policies, string nativeActor, string worktreeRoot)
{
    internal Connection Connection => connection;
    internal Task<Result<StoreSnapshot>> SnapshotAsync(CancellationToken ct) => store.ReadAsync(connection, ct);
    internal Task<Result<ProcessSchema>> SchemaAsync(CancellationToken ct) => provider.DiscoverSchemaAsync(connection, ct);

    internal async Task<Result<WorkItem>> ReadAsync(ItemKey key, CancellationToken ct)
    {
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<WorkItem>.Fail(stored.Error);
        key = Resolve(stored.Value, key);
        if (key.IsLocal) return Result<WorkItem>.Fail(ErrorCode.InvalidInput, "A local draft is not a native item observation.");
        var read = await provider.ReadAsync(connection, [key], ct, captureOnly: true).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<WorkItem>.Fail(read.Error);
        if (read.Value.Items.Length != 1 || read.Value.Items[0].Key != key)
            return Result<WorkItem>.Fail(ErrorCode.Unavailable, "The provider did not observe the exact requested item.");
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<WorkItem>.Fail(current.Error);
        var complete = read.Value.Items.Where(i => i.RelationshipsComplete).Select(i => i.Key).ToHashSet();
        var next = Copy(current.Value, current.Value.Version + 1,
            items: current.Value.Items.Where(i => i.Key != key).Concat(read.Value.Items),
            relationships: current.Value.Relationships.Where(r => r.Origin == RelationshipOrigin.Local
                || !complete.Contains(r.From) && !complete.Contains(r.To))
                .Concat(read.Value.Relationships.Where(r => r.Origin == RelationshipOrigin.Provider)).Distinct());
        var committed = await store.CommitAsync(connection, current.Value.Version, next, ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<WorkItem>.Ok(read.Value.Items[0]) : Result<WorkItem>.Fail(committed.Error);
    }

    internal async Task<Result<Draft>> StageSeedAsync(string type, FieldValues fields, CancellationToken ct, Guid? id = null)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<Draft>.Fail(read.Error);
        var snapshot = read.Value;
        var key = ItemKey.Local(id ?? Guid.NewGuid());
        if (snapshot.Mappings.Any(m => m.Local == key))
            return Result<Draft>.Fail(ErrorCode.Conflict, "This local identity is already published; edit its mapped native draft.");
        var previous = snapshot.Drafts.FirstOrDefault(d => d.Key == key);
        if (previous is not null && previous.Type != type)
            return Result<Draft>.Fail(ErrorCode.Conflict, "The retained seed type differs; no existing intent was replaced.");
        var draft = new Draft(key, type, null, previous?.Changes.Overlay(fields) ?? fields);
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, drafts: snapshot.Drafts.Where(d => d.Key != key).Append(draft)), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<Draft>.Ok(draft) : Result<Draft>.Fail(committed.Error);
    }

    internal async Task<Result<Draft>> StageEditAsync(ItemKey key, FieldValues fields, CancellationToken ct, string? typeOverride = null)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var remote = await ReadAsync(key, ct).ConfigureAwait(false);
        if (!remote.IsSuccess) return Result<Draft>.Fail(remote.Error);
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<Draft>.Fail(read.Error);
        var snapshot = read.Value;
        var previous = snapshot.Drafts.FirstOrDefault(d => d.Key == remote.Value.Key);
        var draft = new Draft(remote.Value.Key, typeOverride ?? previous?.Type ?? remote.Value.Type,
            previous?.Baseline ?? remote.Value, previous?.Changes.Overlay(fields) ?? fields);
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, drafts: snapshot.Drafts.Where(d => d.Key != draft.Key).Append(draft)), ct).ConfigureAwait(false);
        return committed.IsSuccess ? Result<Draft>.Ok(draft) : Result<Draft>.Fail(committed.Error);
    }

    internal async Task<Result<CoreDraftPreview>> PreviewDraftAsync(ItemKey key, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var read = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!read.IsSuccess) return Result<CoreDraftPreview>.Fail(read.Error);
        var draft = read.Value.Drafts.FirstOrDefault(d => d.Key == Resolve(read.Value, key));
        if (draft is null) return Result<CoreDraftPreview>.Fail(ErrorCode.NotFound, "No retained draft exists for the exact target.");
        var discovery = await provider.DiscoverSchemaAsync(connection, ct).ConfigureAwait(false);
        if (!discovery.IsSuccess) return Result<CoreDraftPreview>.Fail(discovery.Error);
        var schema = discovery.Value;
        Op op;
        if (draft.Key.IsLocal)
            op = new("create", OpKind.Create, draft.Key, fields: draft.Changes, type: draft.Type,
                concurrency: ConcurrencyMode.ObservedNonAtomic);
        else
        {
            var remote = await ReadAsync(draft.Key, ct).ConfigureAwait(false);
            if (!remote.IsSuccess) return Result<CoreDraftPreview>.Fail(remote.Error);
            var merge = Model.Merge(draft, remote.Value);
            if (!merge.IsSuccess) return Result<CoreDraftPreview>.Fail(merge.Error);
            if (!merge.Value.Conflicts.IsEmpty)
                return Result<CoreDraftPreview>.Fail(new(ErrorCode.Conflict, "The retained draft has genuine concurrent field conflicts.")
                { Evidence = merge.Value.Conflicts });
            var explicitType = draft.Type != draft.Baseline!.Type;
            if (explicitType && remote.Value.Type != draft.Baseline.Type && remote.Value.Type != draft.Type)
                return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The native type changed differently from the authored type intent.");
            var type = explicitType && remote.Value.Type != draft.Type ? draft.Type : null;
            var gates = Model.CloseGates(schema, remote.Value, merge.Value.Fields, type);
            if (!gates.IsSuccess) return Result<CoreDraftPreview>.Fail(gates.Error);
            if (gates.Value.Any(g => draft.Changes.TryGetValue(g, out var authored)
                && merge.Value.Fields.TryGetValue(g, out var merged) && !authored.Equals(merged)))
                return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "An authored close-gate value changed remotely; no replacement judgment was inferred.");
            var fields = new FieldValues(draft.Changes.Where(f => merge.Value.Fields.TryGetValue(f.Key, out var value)
                && (gates.Value.Contains(f.Key) || !remote.Value.Fields.TryGetValue(f.Key, out var current) || !value.Equals(current)))
                .Select(f => KeyValuePair.Create(f.Key, merge.Value.Fields[f.Key])));
            if (fields.Count == 0 && type is null) return Result<CoreDraftPreview>.Ok(new(draft, null));
            op = new("edit", OpKind.Batch, draft.Key, remote.Value.Revision, fields, type,
                concurrency: provider.Capabilities.ExactRevisions ? ConcurrencyMode.ExactRevision : ConcurrencyMode.ObservedNonAtomic);
        }
        var policy = await policies.CaptureAsync(connection, nativeActor, schema, ct).ConfigureAwait(false);
        if (!policy.IsSuccess) return Result<CoreDraftPreview>.Fail(policy.Error);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreDraftPreview>.Fail(current.Error);
        if (!current.Value.Drafts.Any(d => SameDraft(d, draft)))
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The local draft changed during preview; no intent was silently rebased.");
        var proposal = new Proposal(connection, schema.Fingerprint, current.Value.Version, [op],
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var checkedPolicy = await CheckPolicyAsync(proposal, schema, policy.Value, ct).ConfigureAwait(false);
        if (!checkedPolicy.IsSuccess) return Result<CoreDraftPreview>.Fail(checkedPolicy.Error);
        current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<CoreDraftPreview>.Fail(current.Error);
        if (!current.Value.Drafts.Any(d => SameDraft(d, draft)))
            return Result<CoreDraftPreview>.Fail(ErrorCode.Conflict, "The retained draft changed during policy acquisition.");
        proposal = new(connection, schema.Fingerprint, current.Value.Version, [op],
            policy.Value.Document.Identity, policy.Value.Document.Revision);
        var errors = Model.Validate(proposal, proposal.Digest, schema, current.Value, provider.Capabilities);
        return errors.IsEmpty ? Result<CoreDraftPreview>.Ok(new(draft, proposal))
            : Result<CoreDraftPreview>.Fail(errors[0] with { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
    }

    internal async Task<Result<CoreExecution>> SaveDraftAsync(CoreDraftPreview preview, string? confirmation, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        CoreExecution execution;
        if (preview.Proposal is { } proposal)
        {
            var applied = await ApplyAsync(proposal, confirmation ?? "", ct).ConfigureAwait(false);
            if (!applied.IsSuccess || !applied.Value.EffectsVerified) return applied;
            execution = applied.Value;
        }
        else
        {
            var fresh = await PreviewDraftAsync(preview.Draft.Key, ct).ConfigureAwait(false);
            if (!fresh.IsSuccess) return Result<CoreExecution>.Fail(fresh.Error);
            if (fresh.Value.Proposal is not null || !SameDraft(fresh.Value.Draft, preview.Draft))
                return Result<CoreExecution>.Fail(ErrorCode.Conflict, "The no-change preview is stale; review fresh intent instead.");
            execution = new([], AlreadyVerified: true);
        }
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Retained(current.Error);
        var snapshot = current.Value;
        var draft = snapshot.Drafts.FirstOrDefault(d => d.Key == preview.Draft.Key);
        if (draft is null) return Result<CoreExecution>.Ok(execution);
        var drafts = snapshot.Drafts.Where(d => d.Key != draft.Key);
        var changed = !SameDraft(draft, preview.Draft);
        if (changed)
        {
            var receipt = execution.Receipts.LastOrDefault(r => r.Status == OpStatus.Verified);
            var remoteKey = Resolve(snapshot, draft.Key);
            if (receipt?.Revision is null || remoteKey.IsLocal)
                return Retained(new(ErrorCode.Conflict, "Verified effects did not consume later local draft edits."));
            var baseline = snapshot.Items.FirstOrDefault(i => i.Key == remoteKey && i.Revision == receipt.Revision)
                ?? new WorkItem(remoteKey, preview.Proposal!.Operations[0].Type ?? preview.Draft.Type,
                    preview.Proposal.Operations[0].Fields, receipt.Revision);
            var type = draft.Baseline is not null && draft.Type != draft.Baseline.Type ? draft.Type : baseline.Type;
            if (snapshot.Drafts.Any(d => d.Key == remoteKey && d.Key != draft.Key))
                return Retained(new(ErrorCode.Conflict, "A distinct mapped native draft already exists; neither intent was replaced."));
            drafts = drafts.Append(new(remoteKey, type, baseline, draft.Changes));
        }
        var committed = await store.CommitAsync(connection, snapshot.Version,
            Copy(snapshot, snapshot.Version + 1, drafts: drafts), ct).ConfigureAwait(false);
        if (!committed.IsSuccess) return Retained(committed.Error);
        return changed ? Retained(new(ErrorCode.Conflict, "Remote effects verified; later local edits remain as a separate draft."))
            : Result<CoreExecution>.Ok(execution);

        Result<CoreExecution> Retained(Error error) => Result<CoreExecution>.Ok(execution with
        {
            Error = error, DraftRetained = true,
        });
    }

    internal async Task<Result<CoreExecution>> ApplyAsync(Proposal proposal, string confirmation, CancellationToken ct)
    {
        if (confirmation != proposal.Digest)
            return Result<CoreExecution>.Fail(ErrorCode.DigestMismatch, "Confirmation must equal the exact retained proposal digest.");
        if (proposal.Connection != connection)
            return Result<CoreExecution>.Fail(ErrorCode.Conflict, "The proposal belongs to another immutable connection.");
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var stored = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!stored.IsSuccess) return Result<CoreExecution>.Fail(stored.Error);
        if (!stored.Value.Proposals.Any(p => p.Digest == proposal.Digest))
        {
            var policy = await CheckCurrentPolicyAsync(proposal, ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Result<CoreExecution>.Fail(policy.Error);
            var imported = await provider.ImportAsync(proposal, confirmation, ct).ConfigureAwait(false);
            if (!imported.IsSuccess) return Result<CoreExecution>.Fail(imported.Error);
        }
        var receipts = ImmutableArray.CreateBuilder<Receipt>();
        var dispatched = false;
        for (var ordinal = 0; ordinal < proposal.Operations.Length; ordinal++)
        {
            var current = await SnapshotAsync(ct).ConfigureAwait(false);
            if (!current.IsSuccess) return Finish(current.Error);
            var row = current.Value.Journal.Single(j => j.Digest == proposal.Digest && j.Operation == ordinal);
            if (row.Status == OpStatus.Verified) { receipts.Add(row.Receipts[^1]); continue; }
            if (row.Status is OpStatus.Failed or OpStatus.Partial or OpStatus.Indeterminate)
            {
                receipts.Add(row.Receipts[^1]);
                return Finish(row.Receipts[^1].Error);
            }
            var policy = await CheckCurrentPolicyAsync(proposal, ct).ConfigureAwait(false);
            if (!policy.IsSuccess) return Finish(policy.Error);
            Result<Receipt> result;
            if (row.Status == OpStatus.Planned)
            {
                result = await provider.ApplyAsync(proposal, ordinal, Guid.NewGuid(), ct).ConfigureAwait(false);
                dispatched = true;
            }
            else result = await provider.RecoverAsync(proposal, ordinal, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return Finish(result.Error);
            var receipt = result.Value;
            if (receipt.Status == OpStatus.Applied)
            {
                var verified = await provider.VerifyAsync(proposal, receipt, ct).ConfigureAwait(false);
                if (!verified.IsSuccess) { receipts.Add(receipt); return Finish(verified.Error); }
                receipt = verified.Value;
            }
            receipts.Add(receipt);
            if (receipt.Status != OpStatus.Verified) return Finish(receipt.Error);
        }
        return Finish(null);

        Result<CoreExecution> Finish(Error? error) => Result<CoreExecution>.Ok(new(receipts.ToImmutable(), error,
            AlreadyVerified: error is null && !dispatched));
    }

    private async Task<Result<bool>> CheckCurrentPolicyAsync(Proposal proposal, CancellationToken ct)
    {
        var discovery = await provider.DiscoverSchemaAsync(connection, ct).ConfigureAwait(false);
        if (!discovery.IsSuccess) return Result<bool>.Fail(discovery.Error);
        if (discovery.Value.Fingerprint != proposal.SchemaFingerprint)
            return Result<bool>.Fail(ErrorCode.SchemaChanged, "The actual native schema differs from the reviewed proposal.");
        var capture = await policies.CaptureAsync(connection, nativeActor, discovery.Value, ct).ConfigureAwait(false);
        return capture.IsSuccess ? await CheckPolicyAsync(proposal, discovery.Value, capture.Value, ct).ConfigureAwait(false)
            : Result<bool>.Fail(capture.Error);
    }

    private async Task<Result<bool>> CheckPolicyAsync(Proposal proposal, ProcessSchema schema, CapturedDeclaredPolicy policy, CancellationToken ct)
    {
        if (proposal.PolicyIdentity != policy.Document.Identity || proposal.PolicyRevision != policy.Document.Revision)
            return Result<bool>.Fail(ErrorCode.SchemaChanged, "The actual declared policy presence or bytes differ from the reviewed intent.");
        if (policy.Policy.Rules.IsEmpty) return Result<bool>.Ok(true);
        var captured = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!captured.IsSuccess) return Result<bool>.Fail(captured.Error);
        var shapes = policy.Policy.Rules.Select(r => r.Kind == RuleKind.ParentType ? RelationShape.Hierarchy : RelationShape.Dependency).ToHashSet();
        var kinds = schema.Relations.Where(r => shapes.Contains(r.Shape)).Select(r => r.Reference).ToHashSet(StringComparer.Ordinal);
        var pending = new Queue<ItemKey>(proposal.Operations.SelectMany(o => o.Other is null ? [o.Item] : new[] { o.Item, o.Other }).Distinct());
        var items = new Dictionary<ItemKey, WorkItem>();
        var edges = new HashSet<Relationship>();
        var bound = policy.Policy.Rules.Min(r => r.MaxNodes);
        while (pending.TryDequeue(out var key))
        {
            key = Resolve(captured.Value, key);
            if (items.ContainsKey(key)) continue;
            if (items.Count >= bound) return Result<bool>.Fail(ErrorCode.Unavailable, "Mechanical policy acquisition exceeded its declared evidence bound.");
            if (key.IsLocal)
            {
                var local = captured.Value.Drafts.FirstOrDefault(d => d.Key == key);
                if (local is null) return Result<bool>.Fail(ErrorCode.Unavailable, "A local policy endpoint was not observed.");
                items.Add(key, new(key, local.Type, local.Changes, null, true));
            }
            else
            {
                var read = await provider.ReadAsync(connection, [key], ct, captureOnly: true).ConfigureAwait(false);
                if (!read.IsSuccess) return Result<bool>.Fail(read.Error);
                var item = read.Value.Items.SingleOrDefault(i => i.Key == key);
                if (item?.RelationshipsComplete != true)
                    return Result<bool>.Fail(ErrorCode.Unavailable, "Complete native policy endpoint observations are unavailable.");
                items.Add(key, item);
                foreach (var edge in read.Value.Relationships.Where(r => kinds.Contains(r.Kind))) edges.Add(edge);
            }
            foreach (var edge in captured.Value.Relationships.Where(r => r.Origin == RelationshipOrigin.Local && !r.Deleted
                && kinds.Contains(r.Kind) && (r.From == key || r.To == key))) edges.Add(edge);
            foreach (var edge in edges.Where(e => e.From == key || e.To == key))
            {
                if (!items.ContainsKey(edge.From)) pending.Enqueue(edge.From);
                if (!items.ContainsKey(edge.To)) pending.Enqueue(edge.To);
            }
        }
        var before = new StoreSnapshot(captured.Value.Version, items.Values, edges);
        var proposed = edges.ToHashSet();
        foreach (var op in proposal.Operations.Where(o => o.Kind is OpKind.Relate or OpKind.Unrelate))
        {
            var edge = new Relationship(op.Item, op.Other!, op.Relation!, op.Origin);
            if (op.Kind == OpKind.Relate) proposed.Add(edge);
            else proposed.RemoveWhere(e => e.From == edge.From && e.To == edge.To && e.Kind == edge.Kind && e.Origin == edge.Origin);
        }
        var after = new StoreSnapshot(before.Version, items.Values.Select(item => proposal.Operations
            .Where(o => o.Item == item.Key && o.Kind == OpKind.Batch && o.Type is not null)
            .Aggregate(item, (value, op) => new(value.Key, op.Type!, value.Fields, value.Revision, value.RelationshipsComplete))),
            proposed);
        var blocking = Model.Evaluate(policy.Policy, before, after, schema).Where(f => f.Blocks).ToArray();
        return blocking.Length == 0 ? Result<bool>.Ok(true)
            : Result<bool>.Fail(new(ErrorCode.Unavailable, "Declared mechanical policy refused the observed effect.")
            { Evidence = blocking.Select(f => f.RuleId + ": " + f.Message).ToImmutableArray() });
    }

    private static ItemKey Resolve(StoreSnapshot snapshot, ItemKey key)
        => snapshot.Mappings.FirstOrDefault(m => m.Local == key)?.Remote ?? key;
    private static bool SameDraft(Draft left, Draft right) => left.Key == right.Key && left.Type == right.Type
        && left.Baseline?.Revision == right.Baseline?.Revision && left.Changes.Equals(right.Changes);
    private static StoreSnapshot Copy(StoreSnapshot snapshot, long version, IEnumerable<WorkItem>? items = null,
        IEnumerable<Relationship>? relationships = null, IEnumerable<Draft>? drafts = null)
        => new(version, items ?? snapshot.Items, relationships ?? snapshot.Relationships, drafts ?? snapshot.Drafts,
            snapshot.Proposals, snapshot.Journal, snapshot.Mappings, snapshot.Benches);
}
