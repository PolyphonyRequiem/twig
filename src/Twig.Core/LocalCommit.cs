namespace Twig.Core;

internal static class LocalCommit
{
    // The reducer receives current content on every attempt. It must contain only
    // the local change; provider reads, credentials and dispatch do not belong here.
    internal static async Task<Result<long>> ReduceAsync(IStore store, Connection connection,
        Func<StoreSnapshot, Result<StoreSnapshot>> reduce, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var current = await store.ReadAsync(connection, ct);
            if (!current.IsSuccess) return Result<long>.Fail(current.Error);
            var next = reduce(current.Value);
            if (!next.IsSuccess) return Result<long>.Fail(next.Error);
            var result = await store.CommitAsync(connection, current.Value.Version, next.Value, ct);
            if (result.IsSuccess || !result.Error.StoreVersionChanged || attempt == 3) return result;
        }
    }

    internal static bool ReviewedScalar(Plan plan) => plan.Review is not null
        && plan.Operations is [{ Kind: OpKind.Batch or OpKind.Comment, Links.IsEmpty: true, Other: null, Resource: null, Type: null }];

    internal static Task<Result<long>> AdmitScalarAsync(IStore store, Plan plan, StoreSnapshot captured, StoreSnapshot acquired,
        Guid? attempt, Action<StoreSnapshot> validate, CancellationToken ct)
        => ReduceAsync(store, plan.Connection, current =>
        {
            var op = plan.Operations.Single();
            var observed = acquired.Items.Single(i => i.Key == op.Item);
            var cached = current.Items.SingleOrDefault(i => i.Key == op.Item)?.Revision;
            bool Incident(Relationship edge) => edge.From == op.Item || edge.To == op.Item;
            if (observed.Revision != op.ExpectedRevision
                || cached != observed.Revision && cached != captured.Items.SingleOrDefault(i => i.Key == op.Item)?.Revision
                || !current.Relationships.Where(Incident).ToHashSet().SetEquals(captured.Relationships.Where(Incident)))
                return Result<StoreSnapshot>.Fail(ErrorCode.Conflict, "The reviewed target or incident relationships changed.");
            validate(current);
            var plans = current.Plans;
            var journal = current.Journal;
            if (attempt is null)
            {
                if (plans.Any(p => p.Digest == plan.Digest))
                    return Result<StoreSnapshot>.Fail(ErrorCode.Conflict, "A retained plan cannot be reset.");
                plans = plans.Add(plan);
                journal = journal.Add(JournalEntry.Planned(plan.Digest, 0));
            }
            else
            {
                var row = journal.SingleOrDefault(j => j.Digest == plan.Digest && j.Operation == 0);
                if (row is null) return Result<StoreSnapshot>.Fail(ErrorCode.Conflict, "The reviewed operation is not retained.");
                var began = row.Begin(attempt.Value);
                if (!began.IsSuccess) return Result<StoreSnapshot>.Fail(began.Error);
                journal = journal.Replace(row, began.Value);
            }
            return Result<StoreSnapshot>.Ok(new(current.Version + 1, current.Items.Where(i => i.Key != op.Item).Append(observed),
                current.Relationships.Where(e => e.Origin == RelationshipOrigin.Local || !Incident(e))
                    .Concat(acquired.Relationships.Where(e => e.Origin == RelationshipOrigin.Provider && Incident(e))),
                current.RetainedItems, plans, journal, current.Mappings, current.Benches));
        }, ct);

    internal static Task<Result<long>> ReceiptAsync(IStore store, Plan plan, Receipt receipt,
        Func<StoreSnapshot, StoreSnapshot> mirror, Relationship? localEdge = null)
        => ReduceAsync(store, plan.Connection, stored =>
        {
            var row = stored.Journal.SingleOrDefault(j => j.Digest == receipt.Digest && j.Operation == receipt.Operation);
            if (row is null || row.Attempt != receipt.Attempt || plan.Digest != receipt.Digest)
                return Result<StoreSnapshot>.Fail(ErrorCode.Conflict, "Receipt does not belong to the original operation.");
            var recorded = row.Record(receipt);
            if (!recorded.IsSuccess) return Result<StoreSnapshot>.Fail(recorded.Error);
            var op = plan.Operations[receipt.Operation];
            var mappings = stored.Mappings;
            if (op.Kind == OpKind.Create && receipt.Status == OpStatus.Applied)
                mappings = mappings.Add(new(op.Item, receipt.PublishedKey!, plan.Digest, receipt.Operation));
            var observed = mirror(stored);
            var relationships = observed.Relationships;
            if (localEdge is not null)
            {
                var prior = relationships.SingleOrDefault(r => r.Origin == RelationshipOrigin.Local
                    && r.From == localEdge.From && r.To == localEdge.To && r.Kind == localEdge.Kind);
                if (prior?.LocalVersion != op.ExpectedLocalVersion)
                    return Result<StoreSnapshot>.Fail(ErrorCode.Conflict, "Local relationship changed after admission.");
                relationships = [.. relationships.Where(r => r != prior), localEdge];
            }
            return Result<StoreSnapshot>.Ok(new(stored.Version + 1, observed.Items, relationships,
                stored.RetainedItems, stored.Plans, stored.Journal.Select(j => j == row ? recorded.Value : j),
                mappings, stored.Benches));
        }, CancellationToken.None);
}
