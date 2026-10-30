using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    public Task<Result<long>> ImportAsync(Proposal proposal, string confirmation, CancellationToken ct = default)
        => Guard(() => WithSession(proposal.Connection, async (bound, token) =>
        {
            var stored = await Snapshot(token);
            RefuseChangedActor(stored, bound);
            RefuseUnresolvedTargets(proposal, stored);
            var schema = await Discover(bound, token);
            var acquired = await AcquireInputs(bound, schema, proposal, stored, token);
            var errors = Model.Validate(proposal, confirmation, schema.Schema, acquired, schema.Capabilities);
            Check(errors);
            foreach (var op in proposal.Operations) await Shape(bound, schema, op, Guid.Empty, token);
            if (stored.Proposals.Any(p => p.Digest == proposal.Digest))
                throw Wire.Refuse(ErrorCode.Conflict, "Retained intent cannot be reset.");
            await bound.Session.ValidateAsync(token);
            var result = await _store.CommitAsync(_connection, stored.Version,
                Copy(acquired, stored.Version + 1, proposals: stored.Proposals.Add(proposal),
                    journal: stored.Journal.AddRange(proposal.Operations.Select((_, n) => JournalEntry.Planned(proposal.Digest, n)))), token);
            if (!result.IsSuccess) throw new GitHubFailure(result.Error);
            return result.Value;
        }, ct));

    public Task<Result<Receipt>> ApplyAsync(Proposal proposal, int operation, Guid attempt, CancellationToken ct)
        => Guard(() => WithSession(proposal.Connection, async (bound, token) =>
        {
            if (attempt == Guid.Empty || operation < 0 || operation >= proposal.Operations.Length)
                throw Wire.Refuse(ErrorCode.InvalidInput, "Valid operation and nonempty attempt UUID required.");
            var stored = await Snapshot(token);
            RefuseChangedActor(stored, bound);
            RefuseUnresolvedTargets(proposal, stored);
            var schema = await Discover(bound, token);
            var acquired = await AcquireInputs(bound, schema, proposal, stored, token);
            Check(Model.ValidateEffect(proposal, operation, schema.Schema, acquired, schema.Capabilities));
            var op = proposal.Operations[operation];
            var shape = await Shape(bound, schema, op, attempt, token);
            if (op.ExpectedRevision is not null)
            {
                var latest = await ReadItem(bound, schema, op.Item, token);
                if (latest.Item.Revision != op.ExpectedRevision) throw Wire.Refuse(ErrorCode.Conflict, "Opaque observation moved before dispatch.");
            }
            await bound.Session.ValidateAsync(token);
            var row = stored.Journal.Single(j => j.Digest == proposal.Digest && j.Operation == operation);
            var began = row.Begin(attempt);
            if (!began.IsSuccess) throw new GitHubFailure(began.Error);
            var fenced = await _store.CommitAsync(_connection, stored.Version,
                Copy(acquired, stored.Version + 1, journal: Replace(stored.Journal, began.Value)), token);
            if (!fenced.IsSuccess) throw new GitHubFailure(fenced.Error);
            var evidence = new List<string>
            {
                $"actor:{bound.Actor}", $"authority:{bound.Authority}",
                $"attempt:{attempt:D}", $"request-sha256:{Wire.Hash(shape.Payload)}",
                op.Origin == RelationshipOrigin.Local ? "transport:local-store-cas" : "concurrency:observed-non-atomic; no remote CAS or atomic batch guarantee",
            };
            if (op.Origin == RelationshipOrigin.Local)
            {
                var receipt = new Receipt(proposal.Digest, operation, attempt, OpStatus.Applied, evidence);
                var edge = Edge(op, schema) with { LocalVersion = (op.ExpectedLocalVersion ?? 0) + 1, Deleted = op.Kind == OpKind.Unrelate };
                return await PersistReceipt(proposal, receipt, null, edge);
            }
            Receipt outcome;
            var dispatched = false;
            try
            {
                var intent = new GitHubWriteIntent(proposal, operation, attempt, bound.Endpoint, "POST", "application/json", Wire.Utf8(shape.Payload));
                var admitted = await bound.Session.AdmitAsync(intent, token);
                if (!admitted.IsSuccess) throw new GitHubFailure(admitted.Error);
                using var admission = admitted.Value;
                await bound.Session.ValidateAsync(token);
                using var request = Request(bound, shape.Payload);
                token.ThrowIfCancellationRequested();
                dispatched = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var bytes = await Wire.Response(response, token);
                var text = Wire.Utf8(bytes);
                evidence.Add($"http:{(int)response.StatusCode}");
                evidence.Add($"github-response:{text}");
                await admission.ObserveAsync((int)response.StatusCode, text, CancellationToken.None);
                // A complete HTTP-layer rejection establishes no mutation execution. GraphQL errors
                // may accompany executed resolver effects and are never treated as a no-effect proof.
                if ((int)response.StatusCode is 400 or 401 or 403 or 404 or 405 or 415 or 422 or 429)
                    outcome = new(proposal.Digest, operation, attempt, OpStatus.Failed, evidence,
                        error: new(ErrorCode.Unavailable, $"Definitive HTTP {(int)response.StatusCode} rejection."));
                else if ((int)response.StatusCode != 200)
                    outcome = Unknown("No definitive native acknowledgement.");
                else
                {
                    var payload = Wire.Member(Wire.Data(Wire.Parse(bytes)), shape.Mutation);
                    if (Wire.Text(payload, "clientMutationId") != attempt.ToString("D"))
                        throw Wire.Invalid("Acknowledgement is not attributable to this exact request.");
                    ItemKey? published = null;
                    if (op.Kind is OpKind.Create or OpKind.Batch)
                    {
                        var issue = Wire.Member(payload, "issue");
                        Repository(Wire.Member(issue, "repository"));
                        var key = Key(Wire.Text(issue, "id"));
                        if (op.Kind == OpKind.Batch && key != op.Item) throw Wire.Invalid("Acknowledgement target changed.");
                        if (op.Kind == OpKind.Create) published = key;
                    }
                    if (op.Kind == OpKind.Comment)
                    {
                        var comment = Wire.Member(payload, "commentEdge");
                        var node = Wire.Member(comment, "node");
                        evidence.Add("comment-id:" + Wire.Id(Wire.Text(node, "id")));
                    }
                    outcome = new(proposal.Digest, operation, attempt, OpStatus.Applied, evidence, publishedKey: published);
                }
            }
            catch (Exception ex) when (ex is GitHubFailure or HttpRequestException or OperationCanceledException or IOException || Malformed(ex))
            {
                evidence.Add((dispatched ? "after-dispatch:" : "no-dispatch:") + ex.GetType().Name);
                outcome = dispatched ? Unknown("Mutation may have executed; no blind replay.")
                    : new(proposal.Digest, operation, attempt, OpStatus.Failed, evidence,
                        error: new(ErrorCode.Unavailable, "Host admission/selection refused before mutation dispatch."));
            }
            return await PersistReceipt(proposal, outcome, null);

            Receipt Unknown(string message) => new(proposal.Digest, operation, attempt, OpStatus.Indeterminate, evidence,
                error: new(ErrorCode.Indeterminate, message));
        }, ct, validateAfter: false));

    public Task<Result<Receipt>> VerifyAsync(Proposal proposal, Receipt acknowledgement, CancellationToken ct)
        => Guard(async () =>
        {
            if (proposal.Connection != _connection) throw Wire.Refuse(ErrorCode.Conflict, "Verification binding changed.");
            var stored = await Snapshot(ct);
            var row = stored.Journal.SingleOrDefault(j => j.Digest == proposal.Digest && j.Operation == acknowledgement.Operation);
            if (row is null || row.Status != OpStatus.Applied || row.Attempt != acknowledgement.Attempt
                || row.Receipts.Length != 1 || !SameReceipt(row.Receipts[0], acknowledgement))
                throw Wire.Refuse(ErrorCode.Conflict, "Verification requires the exact durable Applied receipt.");
            Receipt receipt;
            QueryPage? observations = null;
            try
            {
                receipt = await WithSession(proposal.Connection, async (bound, token) =>
                {
                    RefuseChangedActor(stored, bound);
                    if (!acknowledgement.Evidence.Contains($"authority:{bound.Authority}"))
                        throw Wire.Refuse(ErrorCode.Conflict, "Original actor/acquisition binding changed.");
                    var schema = await Discover(bound, token);
                    if (schema.Schema.Fingerprint != proposal.SchemaFingerprint)
                        throw Wire.Refuse(ErrorCode.SchemaChanged, "Readback schema/authority changed.");
                    var op = proposal.Operations[acknowledgement.Operation];
                    var matches = false;
                    Revision? revision = null;
                    if (op.Origin == RelationshipOrigin.Local)
                    {
                        var edge = Edge(op, schema);
                        matches = stored.Relationships.Any(r => r.From == edge.From && r.To == edge.To && r.Kind == edge.Kind
                            && r.Origin == RelationshipOrigin.Local && r.LocalVersion == (op.ExpectedLocalVersion ?? 0) + 1
                            && r.Deleted == (op.Kind == OpKind.Unrelate));
                    }
                    else
                    {
                        var key = op.Kind == OpKind.Create ? acknowledgement.PublishedKey! : op.Item;
                        var keys = op.Other is null ? ImmutableArray.Create(key) : ImmutableArray.Create(key, op.Other);
                        observations = await Read(bound, schema, keys, token);
                        var item = observations.Items.Single(i => i.Key == key);
                        revision = item.Revision;
                        matches = op.Kind switch
                        {
                            OpKind.Create or OpKind.Batch => (op.Type is null || op.Type == item.Type)
                                && op.Fields.All(f => item.Fields.TryGetValue(f.Key, out var value) && value.Equals(f.Value)),
                            OpKind.Relate => observations.Relationships.Contains(Edge(op, schema)),
                            OpKind.Unrelate => !observations.Relationships.Contains(Edge(op, schema)),
                            OpKind.Comment => await CommentMatches(bound, op, acknowledgement, token),
                            _ => false,
                        };
                    }
                    await bound.Session.ValidateAsync(token);
                    return new Receipt(proposal.Digest, acknowledgement.Operation, acknowledgement.Attempt,
                        matches ? OpStatus.Verified : OpStatus.Partial,
                        [$"actor:{bound.Actor}", $"authority:{bound.Authority}",
                            matches ? "Separate readback observed every intended effect; non-atomic races remain possible."
                                : "Separate readback differs from intended effect; no replay."],
                        revision, acknowledgement.PublishedKey,
                        matches ? null : new(ErrorCode.Partial, "Acknowledged effect is not fully observed."));
                }, ct, validateAfter: false);
            }
            catch (Exception ex) when (ex is GitHubFailure or HttpRequestException or OperationCanceledException or IOException || Malformed(ex))
            {
                receipt = new(proposal.Digest, acknowledgement.Operation, acknowledgement.Attempt, OpStatus.Indeterminate,
                    [$"Readback unavailable ({ex.GetType().Name}); original Applied acknowledgement remains."],
                    publishedKey: acknowledgement.PublishedKey, error: new(ErrorCode.Indeterminate, "Readback failure is not non-dispatch evidence."));
            }
            return await PersistReceipt(proposal, receipt, observations);
        });

    public async Task<Result<Receipt>> RecoverAsync(Proposal proposal, int operation, CancellationToken ct = default)
    {
        if (proposal.Connection != _connection || operation < 0 || operation >= proposal.Operations.Length)
            return Result<Receipt>.Fail(ErrorCode.Conflict, "Recovery requires this exact retained connection/ordinal.");
        var stored = await _store.ReadAsync(_connection, ct);
        if (!stored.IsSuccess) return Result<Receipt>.Fail(stored.Error);
        var row = stored.Value.Journal.SingleOrDefault(j => j.Digest == proposal.Digest && j.Operation == operation);
        if (row is null) return Result<Receipt>.Fail(ErrorCode.Conflict, "No retained operation exists.");
        if (row.Status == OpStatus.Applied) return await VerifyAsync(proposal, row.Receipts[0], ct);
        if (row.Status == OpStatus.Applying)
            return Result<Receipt>.Ok(await PersistReceipt(proposal,
                new(proposal.Digest, operation, row.Attempt, OpStatus.Indeterminate,
                    ["Durable Applying has no positive attributable acknowledgement; no create/comment replay or title search was performed."],
                    error: new(ErrorCode.Indeterminate, "Original attempt and local creation UUID remain fenced.")), null));
        return row.Receipts.IsEmpty ? Result<Receipt>.Fail(ErrorCode.Conflict, "Unattempted operation is not recovery.")
            : Result<Receipt>.Ok(row.Receipts[^1]);
    }

    private async Task<bool> CommentMatches(Bound bound, Op op, Receipt acknowledgement, CancellationToken ct)
    {
        var id = acknowledgement.Evidence.Single(e => e.StartsWith("comment-id:", StringComparison.Ordinal))[11..];
        var data = await Graph(bound,
            "query($id: ID!) { node(id: $id) { __typename id ... on IssueComment { body author { ... on User { id } } issue { id repository { id owner { id } } } } } }",
            json => json.WriteString("id", id), ct);
        var comment = Wire.Member(data, "node");
        var issue = Wire.Member(comment, "issue"); Repository(Wire.Member(issue, "repository"));
        return Wire.Text(comment, "__typename") == "IssueComment" && Wire.Text(comment, "id") == id
            && Wire.Text(issue, "id") == op.Item.NativeId
            && Wire.Text(Wire.Member(comment, "author"), "id") == bound.Actor && Wire.Text(comment, "body") == op.Body!.Text;
    }

    private static void Check(ImmutableArray<Error> errors)
    {
        if (!errors.IsEmpty) throw new GitHubFailure(errors[0] with { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
    }

    private async Task<StoreSnapshot> Snapshot(CancellationToken ct)
    {
        var result = await _store.ReadAsync(_connection, ct);
        return result.IsSuccess ? result.Value : throw new GitHubFailure(result.Error);
    }

    private async Task<StoreSnapshot> AcquireInputs(Bound bound, Discovery schema, Proposal proposal, StoreSnapshot stored, CancellationToken ct)
    {
        var keys = proposal.Operations.SelectMany(o => o.Other is null ? new[] { o.Item } : new[] { o.Item, o.Other })
            .Where(k => !k.IsLocal).Distinct().ToImmutableArray();
        return Mirror(stored, await Read(bound, schema, keys, ct), stored.Version);
    }

    private static StoreSnapshot Mirror(StoreSnapshot stored, QueryPage page, long version)
    {
        var keys = page.Items.Select(i => i.Key).ToHashSet();
        return Copy(stored, version, items: stored.Items.Where(i => !keys.Contains(i.Key)).Concat(page.Items),
            relationships: stored.Relationships.Where(r => r.Origin == RelationshipOrigin.Local
                || !keys.Contains(r.From) && !keys.Contains(r.To)).Concat(page.Relationships));
    }

    private static void RefuseChangedActor(StoreSnapshot stored, Bound bound)
    {
        if (stored.Journal.SelectMany(j => j.Receipts).SelectMany(r => r.Evidence).Any(e =>
            e.StartsWith("actor:", StringComparison.Ordinal) && e != $"actor:{bound.Actor}"
            || e.StartsWith("authority:", StringComparison.Ordinal) && e != $"authority:{bound.Authority}"))
            throw Wire.Refuse(ErrorCode.Conflict, "Original native actor or credential-acquisition binding changed.");
    }

    private static void RefuseUnresolvedTargets(Proposal proposal, StoreSnapshot stored)
    {
        foreach (var row in stored.Journal.Where(j => j.Status is OpStatus.Applying or OpStatus.Applied or OpStatus.Partial or OpStatus.Indeterminate))
        {
            var prior = stored.Proposals.Single(p => p.Digest == row.Digest).Operations[row.Operation];
            if (proposal.Operations.Any(op => op.Item == prior.Item || op.Other == prior.Item
                || prior.Other is not null && (op.Item == prior.Other || op.Other == prior.Other)))
                throw Wire.Refuse(ErrorCode.Indeterminate, "An unresolved effect fences this target across replacement digests.");
        }
    }

    private async Task<Receipt> PersistReceipt(Proposal proposal, Receipt receipt, QueryPage? observations, Relationship? localEdge = null)
    {
        var current = await _store.ReadAsync(_connection, CancellationToken.None);
        if (!current.IsSuccess) return Undurable(current.Error);
        var stored = current.Value;
        var row = stored.Journal.Single(j => j.Digest == receipt.Digest && j.Operation == receipt.Operation);
        var recorded = row.Record(receipt);
        if (!recorded.IsSuccess) return Undurable(recorded.Error);
        var mappings = stored.Mappings;
        if (proposal.Operations[receipt.Operation].Kind == OpKind.Create && receipt.Status == OpStatus.Applied)
            mappings = mappings.Add(new(proposal.Operations[receipt.Operation].Item, receipt.PublishedKey!, proposal.Digest, receipt.Operation));
        if (observations is not null) stored = Mirror(stored, observations, stored.Version);
        var relationships = stored.Relationships;
        if (localEdge is not null)
        {
            var prior = relationships.SingleOrDefault(r => r.Origin == RelationshipOrigin.Local
                && r.From == localEdge.From && r.To == localEdge.To && r.Kind == localEdge.Kind);
            if (prior?.LocalVersion != proposal.Operations[receipt.Operation].ExpectedLocalVersion)
                return Undurable(new(ErrorCode.Conflict, "Local relationship CAS changed after durable acquisition."));
            relationships = relationships.Where(r => r != prior).Append(localEdge).ToImmutableArray();
        }
        var next = Copy(stored, stored.Version + 1, relationships: relationships,
            journal: Replace(stored.Journal, recorded.Value), mappings: mappings);
        var result = await _store.CommitAsync(_connection, stored.Version, next, CancellationToken.None);
        return result.IsSuccess ? receipt : Undurable(result.Error);

        Receipt Undurable(Error error) => new(receipt.Digest, receipt.Operation, receipt.Attempt, OpStatus.Indeterminate,
            receipt.Evidence.Add($"Receipt durability failed: {error.Code}; original durable fence remains."),
            receipt.Revision, receipt.PublishedKey, new(ErrorCode.StorageUnavailable, "Outcome could not be committed; no replay."));
    }

    private static ImmutableArray<JournalEntry> Replace(ImmutableArray<JournalEntry> journal, JournalEntry row)
        => journal.Select(j => j.Digest == row.Digest && j.Operation == row.Operation ? row : j).ToImmutableArray();

    private static StoreSnapshot Copy(StoreSnapshot store, long version, IEnumerable<WorkItem>? items = null,
        IEnumerable<Relationship>? relationships = null, IEnumerable<Proposal>? proposals = null,
        IEnumerable<JournalEntry>? journal = null, IEnumerable<PublishMapping>? mappings = null)
        => new(version, items ?? store.Items, relationships ?? store.Relationships, store.Drafts,
            proposals ?? store.Proposals, journal ?? store.Journal, mappings ?? store.Mappings, store.Benches);

    private static bool SameReceipt(Receipt a, Receipt b) => a.Digest == b.Digest && a.Operation == b.Operation
        && a.Attempt == b.Attempt && a.Status == b.Status && a.Revision == b.Revision && a.PublishedKey == b.PublishedKey
        && a.Evidence.SequenceEqual(b.Evidence, StringComparer.Ordinal) && a.Error is null && b.Error is null;
}
