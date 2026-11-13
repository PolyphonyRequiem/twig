using System.Collections.Immutable;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.Native;

/// <summary>Same-machine native protocol boundary. Remote transport/authentication is not inferred.</summary>
public sealed class NativeProvider(NativeAuthority authority, IStore store) : IProvider, IDisposable
{
    public const string ItemType = "Twig.Item";
    public const string Title = "Twig.Title";
    public const string Description = "Twig.Description";
    public const string StateField = "Twig.State";
    public Connection Connection => authority.Connection;
    public ProviderCapabilities Capabilities { get; } = new([OpKind.Create, OpKind.Batch], [], [], true, true);
    public void Dispose() { }

    public Task<Result<bool>> ValidateReadAccessAsync(Connection connection, CancellationToken ct)
        => Guard(connection, async () => { _ = await authority.Read(ct); return true; });

    public Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct)
        => Guard(connection, async () => { _ = await authority.Read(ct); return Schema(); });

    private ProcessSchema Schema()
    {
        State[] states = [new("Open", StateCategory.Proposed), new("Active", StateCategory.InProgress), new("Closed", StateCategory.Completed)];
        return new(Connection, [new(ItemType, StateField,
            [new(Title, FieldKind.Text, Name: "Title"), new(Description, FieldKind.Markdown, Name: "Description"),
                new(StateField, FieldKind.Text, Name: "State")], states,
            states.SelectMany(from => states.Where(to => to != from).Select(to => new Transition(from.Name, to.Name))),
            [new(Title, RuleAction.Required, []), new(StateField, RuleAction.Required, []),
                new(StateField, RuleAction.Default, [], [FieldValue.String("Open")])],
            presentation: new("Twig item", "708090", null))], []);
    }

    public Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct)
        => Guard(connection, async () =>
        {
            if (continuation is not null) throw new NativeFailure(ErrorCode.InvalidInput, "Native query is complete in one snapshot; no cursor is accepted.");
            var items = await authority.Read(ct);
            var selected = ImmutableArray.CreateBuilder<WorkItem>();
            foreach (var item in items)
            {
                var matches = Model.Matches(query, item);
                if (!matches.IsSuccess) throw new NativeFailure(matches.Error.Code, matches.Error.Message);
                if (matches.Value) selected.Add(item);
            }
            return new QueryPage(selected.ToImmutable(), [], null, true);
        });

    public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct, bool captureOnly = false)
        => Guard(connection, async () =>
        {
            if (keys.Any(key => !key.BelongsTo(Connection)))
                throw new NativeFailure(ErrorCode.InvalidInput, "Native reads require exact published source identities; seeds are not authoritative items.");
            var items = await authority.Read(ct);
            var selected = items.Where(item => keys.Contains(item.Key)).ToImmutableArray();
            if (keys.Distinct().Count() != selected.Length)
                throw new NativeFailure(ErrorCode.NotFound, "An exact native item is absent from this authority.");
            return new QueryPage(selected, [], null, true);
        });

    public Task<Result<long>> ImportAsync(Proposal proposal, string confirmation, CancellationToken ct = default)
        => Guard(proposal.Connection, async () =>
        {
            var before = await Snapshot(ct);
            var acquired = Mirror(before, await authority.Read(ct));
            Check(Model.Validate(proposal, confirmation, Schema(), acquired, Capabilities));
            foreach (var op in proposal.Operations) Shape(op);
            Fence(proposal, before);
            if (before.Proposals.Any(value => value.Digest == proposal.Digest))
                throw new NativeFailure(ErrorCode.Conflict, "Retained intent cannot be reset.");
            return await Commit(before, Copy(acquired, before.Version + 1,
                proposals: before.Proposals.Add(proposal),
                journal: before.Journal.AddRange(proposal.Operations.Select((_, n) => JournalEntry.Planned(proposal.Digest, n)))), ct);
        });

    public Task<Result<Receipt>> ApplyAsync(Proposal proposal, int operation, Guid attempt, CancellationToken ct)
        => Guard(proposal.Connection, async () =>
        {
            if (attempt == Guid.Empty) throw new NativeFailure(ErrorCode.InvalidInput, "The first attempt needs its durable UUID.");
            var before = await Snapshot(ct);
            var acquired = Mirror(before, await authority.Read(ct));
            Check(Model.ValidateEffect(proposal, operation, Schema(), acquired, Capabilities));
            var op = proposal.Operations[operation];
            Shape(op);
            Fence(proposal, before);
            var began = before.Journal.Single(row => row.Digest == proposal.Digest && row.Operation == operation).Begin(attempt);
            if (!began.IsSuccess) throw new NativeFailure(began.Error.Code, began.Error.Message);
            await Commit(before, Copy(acquired, before.Version + 1, journal: Replace(before.Journal, began.Value)), ct);
            Receipt receipt;
            try { receipt = await authority.Execute(proposal, operation, attempt, ct); }
            catch (Exception ex) when (ex is NativeFailure or Microsoft.Data.Sqlite.SqliteException or IOException or OperationCanceledException)
            {
                // A failed commit acknowledgement is not proof of no effect. Recovery reads the authority ledger.
                receipt = new(proposal.Digest, operation, attempt, OpStatus.Indeterminate,
                    [$"Native effect acknowledgement unavailable ({ex.GetType().Name}); original Applying fence retained."],
                    error: new(ErrorCode.Indeterminate, "Observe the original attempt; no replay."));
                return receipt;
            }
            return await Persist(proposal, receipt, null);
        });

    public Task<Result<Receipt>> VerifyAsync(Proposal proposal, Receipt acknowledgement, CancellationToken ct)
        => Guard(proposal.Connection, async () =>
        {
            var before = await Snapshot(ct);
            var row = Row(before, proposal, acknowledgement.Operation);
            if (row.Status != OpStatus.Applied || row.Attempt != acknowledgement.Attempt || row.Receipts.Length != 1
                || !Same(row.Receipts[0], acknowledgement))
                throw new NativeFailure(ErrorCode.Conflict, "Verification requires the exact durable Applied acknowledgement.");
            var authoritative = await authority.Recover(proposal, acknowledgement.Operation, acknowledgement.Attempt, ct);
            if (authoritative is null || !Same(authoritative, acknowledgement))
                throw new NativeFailure(ErrorCode.Conflict, "The original attributable native effect is unavailable.");
            var items = await authority.Read(ct);
            var op = proposal.Operations[acknowledgement.Operation];
            var observed = items.SingleOrDefault(item => item.Key == (acknowledgement.PublishedKey ?? op.Item));
            var matches = proposal.SchemaFingerprint == Schema().Fingerprint && observed is not null
                && observed.Revision == acknowledgement.Revision
                && op.Fields.All(field => observed.Fields.TryGetValue(field.Key, out var value) && value.Equals(field.Value));
            var receipt = new Receipt(proposal.Digest, acknowledgement.Operation, acknowledgement.Attempt,
                matches ? OpStatus.Verified : OpStatus.Partial,
                [$"source:{Connection.ResourceScope}", $"principal:{Connection.IdentityRef}", "Separate authoritative readback; no replay."],
                observed?.Revision, acknowledgement.PublishedKey,
                matches ? null : new(ErrorCode.Partial, "The exact native effect is no longer fully observed."));
            return await Persist(proposal, receipt, items);
        });

    public Task<Result<Receipt>> RecoverAsync(Proposal proposal, int operation, CancellationToken ct = default)
        => Guard(proposal.Connection, async () =>
        {
            var before = await Snapshot(ct);
            var row = Row(before, proposal, operation);
            if (row.Status == OpStatus.Applied)
            {
                var verified = await VerifyAsync(proposal, row.Receipts[0], ct);
                if (!verified.IsSuccess) throw new NativeFailure(verified.Error.Code, verified.Error.Message);
                return verified.Value;
            }
            if (row.Status == OpStatus.Applying)
            {
                var original = await authority.Recover(proposal, operation, row.Attempt, ct);
                return await Persist(proposal, original ?? new(proposal.Digest, operation, row.Attempt, OpStatus.Indeterminate,
                    ["No attributable authority receipt for the original Applying attempt; no replay."],
                    error: new(ErrorCode.Indeterminate, "Original attempt remains protected.")), null);
            }
            return row.Receipts.IsEmpty ? throw new NativeFailure(ErrorCode.Conflict, "Unattempted intent is not recovery.") : row.Receipts[^1];
        });

    private async Task<Receipt> Persist(Proposal proposal, Receipt receipt, ImmutableArray<WorkItem>? observations)
    {
        var before = await Snapshot(CancellationToken.None);
        var recorded = Row(before, proposal, receipt.Operation).Record(receipt);
        if (!recorded.IsSuccess) throw new NativeFailure(recorded.Error.Code, recorded.Error.Message);
        var mappings = before.Mappings;
        if (receipt.Status == OpStatus.Applied && receipt.PublishedKey is { } key)
            mappings = mappings.Add(new(proposal.Operations[receipt.Operation].Item, key, proposal.Digest, receipt.Operation));
        var mirrored = observations is { } items ? Mirror(before, items) : before;
        await Commit(before, Copy(mirrored, before.Version + 1, journal: Replace(before.Journal, recorded.Value), mappings: mappings),
            CancellationToken.None);
        return receipt;
    }

    private static void Shape(Op op)
    {
        if (op.Kind is not (OpKind.Create or OpKind.Batch) || op.Type is not (null or ItemType)
            || op.Body is not null || op.Other is not null || op.Relation is not null || op.Resource is not null || !op.Links.IsEmpty
            || op.Fields.Any(field => field.Key switch
            {
                Title or StateField => field.Value.Kind != FieldKind.Text || string.IsNullOrWhiteSpace(field.Value.Text),
                Description => field.Value.Kind != FieldKind.Markdown,
                _ => true
            }))
            throw new NativeFailure(ErrorCode.Unsupported, "Native items support schema-qualified scalar creation and atomic edits only.");
    }

    private static void Fence(Proposal proposal, StoreSnapshot before)
    {
        foreach (var row in before.Journal.Where(row => row.Status is OpStatus.Applying or OpStatus.Applied or OpStatus.Indeterminate or OpStatus.Partial))
        {
            var previous = before.Proposals.Single(value => value.Digest == row.Digest).Operations[row.Operation];
            var target = before.Mappings.SingleOrDefault(mapping => mapping.Local == previous.Item)?.Remote ?? previous.Item;
            if (proposal.Operations.Any(op => op.Item == previous.Item || op.Item == target))
                throw new NativeFailure(ErrorCode.Indeterminate, "An unresolved original operation fences this target across replacement digests.");
        }
    }

    private static JournalEntry Row(StoreSnapshot before, Proposal proposal, int operation)
        => operation >= 0 && operation < proposal.Operations.Length
            && before.Proposals.Any(value => value.Digest == proposal.Digest)
            && before.Journal.SingleOrDefault(row => row.Digest == proposal.Digest && row.Operation == operation) is { } found
                ? found : throw new NativeFailure(ErrorCode.Conflict, "The exact retained proposal and ordinal are required.");
    private Task<Result<T>> Guard<T>(Connection connection, Func<Task<T>> action)
        => NativeGuard.Run(async () =>
        {
            if (connection != Connection) throw new NativeFailure(ErrorCode.Conflict, "Exact native source, destination and principal binding required.");
            return await action();
        });
    private async Task<StoreSnapshot> Snapshot(CancellationToken ct)
    {
        var read = await store.ReadAsync(Connection, ct);
        return read.IsSuccess ? read.Value : throw new NativeFailure(read.Error.Code, read.Error.Message);
    }
    private async Task<long> Commit(StoreSnapshot before, StoreSnapshot next, CancellationToken ct)
    {
        var committed = await store.CommitAsync(Connection, before.Version, next, ct);
        return committed.IsSuccess ? committed.Value : throw new NativeFailure(committed.Error.Code, committed.Error.Message);
    }
    private static void Check(ImmutableArray<Error> errors)
    {
        if (!errors.IsEmpty) throw new NativeFailure(errors[0].Code, string.Join("; ", errors.Select(error => error.Message)));
    }
    private static bool Same(Receipt left, Receipt right) => left.Digest == right.Digest && left.Operation == right.Operation
        && left.Attempt == right.Attempt && left.Status == right.Status && left.PublishedKey == right.PublishedKey
        && left.Revision == right.Revision && left.Evidence.SequenceEqual(right.Evidence) && left.Error == right.Error;
    private static IEnumerable<JournalEntry> Replace(ImmutableArray<JournalEntry> journal, JournalEntry entry)
        => journal.Select(row => row.Digest == entry.Digest && row.Operation == entry.Operation ? entry : row);
    private static StoreSnapshot Mirror(StoreSnapshot before, ImmutableArray<WorkItem> observations)
        => Copy(before, before.Version, items: observations);
    private static StoreSnapshot Copy(StoreSnapshot before, long version, IEnumerable<WorkItem>? items = null,
        IEnumerable<Proposal>? proposals = null, IEnumerable<JournalEntry>? journal = null, IEnumerable<PublishMapping>? mappings = null)
        => new(version, items ?? before.Items, before.Relationships, before.Drafts, proposals ?? before.Proposals,
            journal ?? before.Journal, mappings ?? before.Mappings, before.Benches);
}
