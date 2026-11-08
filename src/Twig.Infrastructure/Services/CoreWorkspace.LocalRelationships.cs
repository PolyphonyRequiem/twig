using Twig.Core;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Services;

internal sealed record CoreLocalRelationship(ItemKey Source, ItemKey Target, RelationKind Kind, Relationship? Edge);

internal sealed partial class CoreWorkspace
{
    internal static ItemKey LocalLogical(StoreSnapshot state, ItemKey key)
        => state.Mappings.FirstOrDefault(mapping => mapping.Local == key
            && state.Proposals.Any(proposal => proposal.Digest == mapping.ProposalDigest
                && mapping.Operation < proposal.Operations.Length
                && proposal.Operations[mapping.Operation] is { Kind: OpKind.Create } create && create.Item == key)
            && state.Journal.Any(row => row.Digest == mapping.ProposalDigest && row.Operation == mapping.Operation
                && row.Status == OpStatus.Verified && row.Receipts.Any(receipt =>
                    receipt.Status == OpStatus.Verified && receipt.PublishedKey == mapping.Remote)))?.Remote ?? key;

    internal static bool LocalTouches(StoreSnapshot state, Relationship edge, ItemKey from, ItemKey to)
        => LocalLogical(state, edge.From) == from && LocalLogical(state, edge.To) == to
            || LocalLogical(state, edge.From) == to && LocalLogical(state, edge.To) == from;

    internal static Result<CoreLocalRelationship> LocalRelationship(StoreSnapshot state, ProcessSchema schema,
        ProviderCapabilities capabilities, ItemKey source, ItemKey target)
    {
        var from = LocalLogical(state, source);
        var to = LocalLogical(state, target);
        if (from == to)
            return Result<CoreLocalRelationship>.Fail(ErrorCode.InvalidInput, "A work item cannot be related to itself.");
        var kinds = schema.Relations.Where(kind => kind.Shape == RelationShape.Association && !kind.Directed
            && capabilities.LocalRelations.Contains(kind.Reference) && !capabilities.NativeRelations.Contains(kind.Reference)).ToArray();
        if (kinds.Length != 1)
            return Result<CoreLocalRelationship>.Fail(ErrorCode.Unsupported,
                "The discovered schema must declare exactly one local-only symmetric Association; native or unavailable relations never fall back to local.");
        var matches = state.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Local
            && edge.Kind == kinds[0].Reference && LocalTouches(state, edge, from, to)).ToArray();
        return matches.Length > 1
            ? Result<CoreLocalRelationship>.Fail(ErrorCode.Conflict, "Conflicting logical local edges; no row was selected or changed.")
            : Result<CoreLocalRelationship>.Ok(new(source, target, kinds[0], matches.SingleOrDefault()));
    }

    internal async Task<Result<Proposal>> PrepareLocalRelationshipAsync(ItemKey source, ItemKey target, bool adding,
        CancellationToken ct, long? expectedStoreVersion = null, string? expectedSchema = null)
    {
        var state = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!state.IsSuccess) return Result<Proposal>.Fail(state.Error);
        var schema = await SchemaAsync(ct).ConfigureAwait(false);
        if (!schema.IsSuccess) return Result<Proposal>.Fail(schema.Error);
        if (expectedStoreVersion is { } version && version != state.Value.Version
            || expectedSchema is not null && expectedSchema != schema.Value.Fingerprint)
            return Result<Proposal>.Fail(ErrorCode.Conflict, "Local identity/edge or discovered schema changed during capture.");
        var selected = LocalRelationship(state.Value, schema.Value, Capabilities, source, target);
        if (!selected.IsSuccess) return Result<Proposal>.Fail(selected.Error);
        var edge = selected.Value.Edge;
        if (adding && edge is { Deleted: false })
            return Result<Proposal>.Fail(ErrorCode.Conflict, "The LOCAL association already exists; no mutation was journaled.");
        if (!adding && (edge is null || edge.Deleted))
            return Result<Proposal>.Fail(ErrorCode.Conflict, "No active LOCAL association exists; no mutation was journaled.");
        // Publication changes logical identity, not the original physical edge or its tombstone CAS.
        var op = new Op("local-related", adding ? OpKind.Relate : OpKind.Unrelate,
            edge?.From ?? source, other: edge?.To ?? target, relation: selected.Value.Kind.Reference,
            origin: RelationshipOrigin.Local, expectedLocalVersion: edge?.LocalVersion);
        var proposal = await PrepareAsync([op], ct).ConfigureAwait(false);
        return !proposal.IsSuccess ? proposal
            : proposal.Value.ExpectedStoreVersion != state.Value.Version || proposal.Value.SchemaFingerprint != schema.Value.Fingerprint
                ? Result<Proposal>.Fail(ErrorCode.Conflict, "Local identity/edge or discovered schema changed during capture; no stale selection was applied.")
                : proposal;
    }

    internal static string LocalEdgeFingerprint(Connection connection, Relationship? edge)
        => edge is null ? "none" : new Proposal(connection, "local-edge-evidence", 0,
            [new("edge", edge.Deleted ? OpKind.Unrelate : OpKind.Relate, edge.From, other: edge.To,
                relation: edge.Kind, origin: edge.Origin, expectedLocalVersion: edge.LocalVersion)]).Digest;

    internal async Task<Result<Receipt>> RecoverLocalRelationshipAsync(Proposal proposal, CancellationToken ct)
    {
        if (proposal.Connection != connection || !NativeLocalRelationshipEvidence.IsLocal(proposal))
            return Result<Receipt>.Fail(ErrorCode.Conflict, "Recovery requires the original single local relationship proposal.");
        using var operation = ConnectionOperationGate.Acquire(worktreeRoot, exclusive: true);
        var current = await SnapshotAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<Receipt>.Fail(current.Error);
        var row = current.Value.Journal.SingleOrDefault(value => value.Digest == proposal.Digest && value.Operation == 0);
        if (row?.Status != OpStatus.Applied)
            return Result<Receipt>.Fail(ErrorCode.Indeterminate, "Only the original durable Applied receipt can continue separate verification; no replay.");
        if (executionRegistryPath is not null)
        {
            if (store is not Twig.Store.Sqlite.SqliteStore native || native.InstanceId != storeInstance)
                return Result<Receipt>.Fail(ErrorCode.StorageUnavailable, "The original authoritative Core store is unavailable.");
            var gate = await NativeCoreExecution.PublishingAsync(executionRegistryPath, ct).ConfigureAwait(false);
            if (!gate.IsSuccess) return Result<Receipt>.Fail(gate.Error);
        }
        var policy = await CheckCurrentPolicyAsync(proposal, ct).ConfigureAwait(false);
        if (!policy.IsSuccess) return Result<Receipt>.Fail(policy.Error);
        using var targets = executionRegistryPath is null ? null : ConnectionOperationGate.AcquireCoreTargets(
            executionRegistryPath, NativeCoreExecution.OperationKeys(proposal.Operations[0], current.Value, connection));
        return await provider.RecoverAsync(proposal, 0, ct).ConfigureAwait(false);
    }
}
