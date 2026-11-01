using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<long>> ImportAsync(Proposal proposal, string confirmation, CancellationToken ct = default)
        => Guard(() => WithSession(proposal.Connection, async (bound, token) =>
        {
            var stored = await Snapshot(token);
            RefuseChangedActor(stored, bound);
            RefuseUnresolvedTargets(proposal, stored);
            var discovery = await Discover(bound, token);
            var acquired = await AcquireInputs(bound, discovery, proposal, stored, token);
            var errors = Model.Validate(proposal, confirmation, discovery.Schema, acquired, Capabilities);
            if (!errors.IsEmpty) throw new AdoFailure(errors[0] with { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
            if (stored.Proposals.Any(p => p.Digest == proposal.Digest))
                throw Wire.Refuse(ErrorCode.Conflict, "Proposal is already retained; it cannot be reimported/reset.");
            var next = Copy(acquired, stored.Version + 1,
                proposals: stored.Proposals.Add(proposal),
                journal: stored.Journal.AddRange(proposal.Operations.Select((_, ordinal) => JournalEntry.Planned(proposal.Digest, ordinal))));
            var result = await _store.CommitAsync(_connection, stored.Version, next, token);
            if (!result.IsSuccess) throw new AdoFailure(result.Error);
            return result.Value;
        }, ct));

    public Task<Result<Receipt>> ApplyAsync(Proposal proposal, int operation, Guid attempt, CancellationToken ct)
        => Guard(() => WithSession(proposal.Connection, async (bound, token) =>
        {
            if (attempt == Guid.Empty || operation < 0 || operation >= proposal.Operations.Length)
                throw Wire.Refuse(ErrorCode.InvalidInput, "A valid ordinal and fresh attempt identity are required.");
            var stored = await Snapshot(token);
            RefuseChangedActor(stored, bound);
            RefuseUnresolvedTargets(proposal, stored);
            var discovery = await Discover(bound, token);
            var acquired = await AcquireInputs(bound, discovery, proposal, stored, token);
            var errors = Model.ValidateEffect(proposal, operation, discovery.Schema, acquired, Capabilities);
            if (!errors.IsEmpty) throw new AdoFailure(errors[0] with { Evidence = errors.Select(e => e.Message).ToImmutableArray() });
            var op = proposal.Operations[operation];
            var (route, payload) = await Patch(bound, discovery, op, token);
            var target = bound.Url(route);
            var method = op.Kind == OpKind.Create ? HttpMethod.Post : HttpMethod.Patch;
            var intent = new AdoWriteIntent(proposal, operation, attempt, target, method.Method,
                "application/json-patch+json", Wire.Utf8(payload));
            var row = stored.Journal.Single(j => j.Digest == proposal.Digest && j.Operation == operation);
            var began = row.Begin(attempt);
            if (!began.IsSuccess) throw new AdoFailure(began.Error);
            var begun = Copy(acquired, stored.Version + 1, journal: Replace(stored.Journal, began.Value));
            var committed = await _store.CommitAsync(_connection, stored.Version, begun, token);
            if (!committed.IsSuccess) throw new AdoFailure(committed.Error);

            var evidence = new List<string>
            {
                $"binding:{_connection.ResourceScope}/{_connection.Destination}/{_connection.IdentityRef}",
                $"actor:{bound.PrincipalId}",
                $"attempt:{attempt:D}",
                $"request-sha256:{Convert.ToHexStringLower(SHA256.HashData(payload))}",
            };
            Receipt receipt;
            QueryPage? acknowledgedItems = null;
            var dispatched = false;
            try
            {
                var admitted = await bound.Session.AdmitAsync(intent, token);
                if (!admitted.IsSuccess) throw new AdoFailure(admitted.Error);
                using var admission = admitted.Value;
                using var request = new HttpRequestMessage(method, target);
                request.Headers.Authorization = bound.Authorization;
                request.Headers.Add("X-MS-Client-Request-Id", attempt.ToString("D"));
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(intent.MediaType);
                token.ThrowIfCancellationRequested();
                dispatched = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                var bytes = await ResponseBytes(response, token);
                var responseText = Wire.Utf8(bytes);
                evidence.Add($"http:{(int)response.StatusCode}");
                evidence.Add($"ado-response:{responseText}");
                // Native acknowledgement durability is independent of the caller canceling after dispatch.
                await admission.ObserveAsync((int)response.StatusCode, responseText, CancellationToken.None);
                if ((int)response.StatusCode is 400 or 401 or 403 or 404 or 405 or 409 or 412 or 415 or 422 or 429)
                    receipt = new(proposal.Digest, operation, attempt, OpStatus.Failed, evidence,
                        error: HttpFailure(response.StatusCode).Error);
                else if ((int)response.StatusCode is not (200 or 201))
                    receipt = Unknown("ADO did not give a definitive item acknowledgement.");
                else
                {
                    var root = JsonSerializer.Deserialize(bytes, TwigJsonContext.Default.JsonElement);
                    Wire.Unique(root);
                    var key = op.Kind == OpKind.Create ? Key(Wire.Positive(root, "id")) : op.Item;
                    var mapped = MapItem(root, bound, discovery, key);
                    if (op.Kind != OpKind.Create && RevisionNumber(op) >= Wire.Positive(root, "rev"))
                        throw Wire.Invalid("Acknowledgement did not advance the exact revision.");
                    receipt = new(proposal.Digest, operation, attempt, OpStatus.Applied, evidence,
                        mapped.Item.Revision, op.Kind == OpKind.Create ? key : null);
                    acknowledgedItems = new([new WorkItem(mapped.Item.Key, mapped.Item.Type, mapped.Item.Fields,
                        mapped.Item.Revision, false, mapped.Item.Resources, false)], [], null, false);
                }
            }
            catch (Exception ex) when (ex is AdoFailure or HttpRequestException or OperationCanceledException or IOException || Malformed(ex))
            {
                evidence.Add($"{(dispatched ? "after-dispatch" : "no-dispatch")}:{ex.GetType().Name}:{ex.Message}");
                receipt = dispatched ? Unknown("Dispatch may have occurred; no replay is permitted.")
                    : new(proposal.Digest, operation, attempt, OpStatus.Failed, evidence,
                        error: ex is AdoFailure failure ? failure.Error : new(ErrorCode.Unavailable, "Admission failed before dispatch."));
            }
            return await PersistReceipt(proposal, receipt, acknowledgedItems);

            Receipt Unknown(string message) => new(proposal.Digest, operation, attempt, OpStatus.Indeterminate, evidence,
                error: new(ErrorCode.Indeterminate, message));
        }, ct));

    public Task<Result<Receipt>> VerifyAsync(Proposal proposal, Receipt acknowledgement, CancellationToken ct)
        => Guard(async () =>
        {
            var stored = await Snapshot(ct);
            var row = stored.Journal.SingleOrDefault(j => j.Digest == proposal.Digest && j.Operation == acknowledgement.Operation);
            if (acknowledgement.Digest != proposal.Digest || row is null || row.Status != OpStatus.Applied
                || row.Attempt != acknowledgement.Attempt || row.Receipts.Length != 1
                || !SameReceipt(row.Receipts[0], acknowledgement))
                throw Wire.Refuse(ErrorCode.Conflict, "Verification requires the exact durable Applied acknowledgement, not supplied identity guesses.");
            Receipt receipt;
            QueryPage? observations = null;
            try
            {
                receipt = await WithSession(proposal.Connection, async (bound, token) =>
                {
                    if (!acknowledgement.Evidence.Contains($"actor:{bound.PrincipalId}"))
                        throw Wire.Refuse(ErrorCode.Conflict, "Readback actor differs from the original attributable acknowledgement.");
                    var discovery = await Discover(bound, token);
                    var op = proposal.Operations[acknowledgement.Operation];
                    var key = op.Kind == OpKind.Create ? acknowledgement.PublishedKey! : op.Item;
                    if (acknowledgement.Revision is null || acknowledgement.Revision.Item != key)
                        throw Wire.Invalid("Acknowledgement lacks its attributable target revision.");
                    var number = RevisionNumber(new("readback", OpKind.Batch, key, acknowledgement.Revision));
                    var root = await Get(bound,
                        $"{bound.Project}/_apis/wit/workitems/{Id(key)}/revisions/{number}?$expand=relations&api-version={Api}", token);
                    var (item, edges) = MapItem(root, bound, discovery, key);
                    if (item.Revision != acknowledgement.Revision) throw Wire.Invalid("Immutable revision readback disagrees with acknowledgement.");
                    var matches = op.Kind switch
                    {
                        OpKind.Create or OpKind.Batch => (op.Type is null || op.Type == item.Type)
                            && op.Fields.All(f => item.Fields.TryGetValue(f.Key, out var value) && value.Equals(f.Value))
                            && (op.Body is null || item.Fields.TryGetValue("System.History", out var note) && note.Text == op.Body.Text)
                            && (op.Kind != OpKind.Create || op.Other is null
                                || edges.Contains(new(Resolve(stored, op.Other), key, op.Relation!, RelationshipOrigin.Provider)))
                            && (op.Kind != OpKind.Create || op.Resource is null
                                || item.ResourcesComplete && item.Resources.Contains(op.Resource))
                            && (op.Kind != OpKind.Create || op.Links.All(link =>
                                edges.Contains(CreationEdge(stored, discovery, key, link)))),
                        OpKind.Comment => item.Fields.TryGetValue("System.History", out var history) && history.Text == op.Body!.Text,
                        OpKind.LinkResource => item.ResourcesComplete && item.Resources.Contains(op.Resource!),
                        OpKind.Relate => edges.Contains(Edge(op, discovery)),
                        OpKind.Unrelate => !edges.Contains(Edge(op, discovery)),
                        _ => false,
                    };
                    var keys = Targets(op).Where(target => target != op.Item).Select(target => Resolve(stored, target))
                        .Prepend(key).Distinct().ToImmutableArray();
                    observations = await Read(bound, discovery, keys, token);
                    if (op.Kind == OpKind.Create)
                        matches &= op.Links.All(link => observations.Relationships.Contains(CreationEdge(stored, discovery, key, link)));
                    if (op.Other is not null && op.Kind != OpKind.Create)
                    {
                        var intended = Edge(op, discovery);
                        matches &= op.Kind == OpKind.Relate
                            ? observations.Relationships.Contains(intended) : !observations.Relationships.Contains(intended);
                    }
                    return new Receipt(proposal.Digest, acknowledgement.Operation, acknowledgement.Attempt,
                        matches ? OpStatus.Verified : OpStatus.Partial,
                        [$"Immutable acknowledged revision {number} was read separately.", $"actor:{bound.PrincipalId}",
                            matches ? "All declared effects were observed." : "Acknowledged effect differs from one or more declared values/relationships."],
                        acknowledgement.Revision, acknowledgement.PublishedKey,
                        matches ? null : new(ErrorCode.Partial, "Readback does not establish all intended effects."));
                }, ct);
            }
            catch (Exception ex) when (ex is AdoFailure or HttpRequestException or OperationCanceledException or IOException || Malformed(ex))
            {
                receipt = new(proposal.Digest, acknowledgement.Operation, acknowledgement.Attempt, OpStatus.Indeterminate,
                    [$"Readback unavailable after durable acknowledgement: {ex.GetType().Name}: {ex.Message}"],
                    acknowledgement.Revision, acknowledgement.PublishedKey,
                    new(ErrorCode.Indeterminate, "Applied is retained; readback failure cannot establish no effect."));
            }
            return await PersistReceipt(proposal, receipt, observations);
        });

    // Recovery only continues a durable acknowledgement's readback. Applying/unknown has no positive creation evidence.
    public async Task<Result<Receipt>> RecoverAsync(Proposal proposal, int operation, CancellationToken ct = default)
    {
        var stored = await _store.ReadAsync(proposal.Connection, ct);
        if (!stored.IsSuccess) return Result<Receipt>.Fail(stored.Error);
        var row = stored.Value.Journal.SingleOrDefault(j => j.Digest == proposal.Digest && j.Operation == operation);
        if (row is null || operation < 0 || operation >= proposal.Operations.Length || proposal.Connection != _connection)
            return Result<Receipt>.Fail(ErrorCode.Conflict, "Recovery requires retained intent in this exact connection.");
        if (row.Status == OpStatus.Applied) return await VerifyAsync(proposal, row.Receipts[0], ct);
        if (row.Status == OpStatus.Applying)
            return Result<Receipt>.Ok(await PersistReceipt(proposal,
                new(proposal.Digest, operation, row.Attempt, OpStatus.Indeterminate,
                    ["Durable Applying attempt has no acknowledged native identity; recovery sent no mutation and performed no title search."],
                    error: new(ErrorCode.Indeterminate, "Non-dispatch is unproven; the attempt and creation UUID stay fenced.")), null));
        return row.Receipts.IsEmpty
            ? Result<Receipt>.Fail(ErrorCode.Conflict, "An unattempted operation is not recovery.")
            : Result<Receipt>.Ok(row.Receipts[^1]);
    }

    private async Task<(string Route, byte[] Payload)> Patch(Bound bound, Discovery schema, Op op, CancellationToken ct)
    {
        if (op.Origin != RelationshipOrigin.Provider || !Capabilities.Operations.Contains(op.Kind))
            throw Wire.Refuse(ErrorCode.Unsupported, "Only explicitly supported native effects are dispatched.");
        if (op.Fields.Keys.Any(f => f is "System.Id" or "System.Rev" or "System.TeamProject" or "System.WorkItemType" or "System.History"))
            throw Wire.Refuse(ErrorCode.Unsupported, "Native identity/type/history fields require their explicit operation shape.");
        if (op.Body is not null && op.Body.Kind is not (FieldKind.Text or FieldKind.Html))
            throw Wire.Refuse(ErrorCode.Unsupported, "ADO history append accepts exact text/HTML; convert Markdown before confirmation.");
        if (op.Resource is not null && (op.Resource is not { } resource
            || !schema.Schema.Relations.Any(r => r.Reference == resource.Relation && r.Shape == RelationShape.Resource)
            || !Uri.TryCreate(resource.Target, UriKind.Absolute, out var locator)
            || resource.Relation == "Hyperlink" && locator.Scheme is not ("http" or "https")
            || resource.Relation == "ArtifactLink" && (locator.Scheme != "vstfs"
                || !resource.Target.StartsWith("vstfs:///", StringComparison.OrdinalIgnoreCase))))
            throw Wire.Refuse(ErrorCode.Unsupported, "Only enabled native artifact/hyperlink shapes with reviewed locators are supported.");
        string? parentRelation = null;
        ItemKey? parentKey = null;
        var current = op.Kind == OpKind.Create ? await Snapshot(ct) : null;
        var creationLinks = new List<(string Relation, ItemKey Target)>();
        foreach (var link in op.Links)
        {
            var target = CreationEndpoint(current!, link.Other);
            var native = schema.Relations.Values.Where(relation => relation.Canonical == link.Relation
                && relation.Forward == link.Outgoing && relation.Shape is RelationShape.Dependency or RelationShape.Association).ToArray();
            if (native.Length != 1)
                throw Wire.Refuse(ErrorCode.Unsupported, "A unique discovered native creation-link direction is required.");
            creationLinks.Add((native[0].Reference, target));
        }
        if (op.Kind == OpKind.Create && op.Other is not null)
        {
            parentKey = CreationEndpoint(current!, op.Other);
            var inverse = schema.Relations.Values.Where(relation => relation.Canonical == op.Relation
                && relation.Shape == RelationShape.Hierarchy && !relation.Forward).ToArray();
            if (inverse.Length != 1) throw Wire.Refuse(ErrorCode.Unsupported, "A unique native inverse hierarchy endpoint is required for atomic creation parenting.");
            parentRelation = inverse[0].Reference;
        }
        int? remove = null;
        if (op.Kind is OpKind.Relate or OpKind.Unrelate)
        {
            var root = await Get(bound, $"{bound.Project}/_apis/wit/workitems/{Id(op.Item)}?$expand=relations&api-version={Api}", ct);
            var mapped = MapItem(root, bound, schema, op.Item);
            if (mapped.Item.Revision != op.ExpectedRevision) throw Wire.Refuse(ErrorCode.Conflict, "Relationship source revision moved.");
            var edge = Edge(op, schema);
            if (op.Kind == OpKind.Relate && mapped.Edges.Contains(edge))
                throw Wire.Refuse(ErrorCode.Conflict, "The native relationship already exists.");
            if (op.Kind == OpKind.Unrelate)
            {
                var relations = root.GetProperty("relations").EnumerateArray().ToArray();
                for (var index = 0; index < relations.Length; index++)
                    if (Wire.Text(relations[index], "rel") == op.Relation
                        && RelationshipTarget(Wire.Text(relations[index], "url"), bound) == op.Other) remove = index;
                if (remove is null) throw Wire.Refuse(ErrorCode.Conflict, "Exact native relationship to remove is absent.");
            }
        }
        var payload = Wire.Json(json =>
        {
            json.WriteStartArray();
            if (op.Kind != OpKind.Create) PatchEntry("test", "/rev", () => json.WriteNumberValue(RevisionNumber(op)));
            if (op.Type is not null && op.Kind != OpKind.Create)
                PatchEntry("add", "/fields/System.WorkItemType", () => json.WriteStringValue(schema.TypeNames[op.Type]));
            foreach (var (field, value) in op.Fields)
                PatchEntry("add", "/fields/" + field.Replace("~", "~0").Replace("/", "~1"), () => Wire.WriteValue(json, value));
            if (op.Kind == OpKind.Comment || op.Kind == OpKind.Batch && op.Body is not null)
                PatchEntry("add", "/fields/System.History", () => json.WriteStringValue(op.Body!.Text));
            if (op.Resource is not null)
                PatchEntry("add", "/relations/-", () =>
                {
                    var reviewed = op.Resource!;
                    json.WriteStartObject(); json.WriteString("rel", reviewed.Relation); json.WriteString("url", reviewed.Target);
                    if (reviewed.Name is not null || reviewed.Comment is not null)
                    {
                        json.WriteStartObject("attributes");
                        if (reviewed.Name is not null) json.WriteString("name", reviewed.Name);
                        if (reviewed.Comment is not null) json.WriteString("comment", reviewed.Comment);
                        json.WriteEndObject();
                    }
                    json.WriteEndObject();
                });
            if (parentRelation is not null)
                PatchEntry("add", "/relations/-", () =>
                {
                    json.WriteStartObject();
                    json.WriteString("rel", parentRelation);
                    json.WriteString("url", bound.Url($"_apis/wit/workItems/{Id(parentKey!)}").AbsoluteUri);
                    json.WriteEndObject();
                });
            foreach (var link in creationLinks)
                PatchEntry("add", "/relations/-", () =>
                {
                    json.WriteStartObject();
                    json.WriteString("rel", link.Relation);
                    json.WriteString("url", bound.Url($"_apis/wit/workItems/{Id(link.Target)}").AbsoluteUri);
                    json.WriteEndObject();
                });
            if (op.Kind == OpKind.Relate)
                PatchEntry("add", "/relations/-", () =>
                {
                    json.WriteStartObject();
                    json.WriteString("rel", op.Relation);
                    json.WriteString("url", bound.Url($"_apis/wit/workItems/{Id(op.Other!)}").AbsoluteUri);
                    json.WriteEndObject();
                });
            if (remove is not null) PatchEntry("remove", $"/relations/{remove}", null);
            json.WriteEndArray();

            void PatchEntry(string verb, string path, Action? write)
            {
                json.WriteStartObject(); json.WriteString("op", verb); json.WriteString("path", path);
                if (write is not null) { json.WritePropertyName("value"); write(); }
                json.WriteEndObject();
            }
        });
        var item = op.Kind == OpKind.Create ? "$" + Wire.Escape(schema.TypeNames[op.Type!]) : Id(op.Item).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ($"{bound.Project}/_apis/wit/workitems/{item}?api-version={Api}", payload);
    }

    private static Relationship Edge(Op op, Discovery schema)
    {
        if (op.Other is null || op.Other == op.Item || op.Relation is null
            || !schema.Relations.TryGetValue(op.Relation, out var kind) || !kind.Forward)
            throw Wire.Refuse(ErrorCode.Unsupported, "Relationship operations use the discovered forward kind and distinct endpoints.");
        var from = op.Item;
        var to = op.Other;
        if (!kind.Directed && string.CompareOrdinal(from.NativeId, to.NativeId) > 0) (from, to) = (to, from);
        return new(from, to, op.Relation, RelationshipOrigin.Provider);
    }

    private ItemKey CreationEndpoint(StoreSnapshot stored, ItemKey endpoint)
    {
        var key = Resolve(stored, endpoint);
        if (key.IsLocal || !key.BelongsTo(_connection))
            throw Wire.Refuse(ErrorCode.Conflict, "The exact creation endpoint has no in-scope publication mapping.");
        if (endpoint.IsLocal)
        {
            var mapping = stored.Mappings.Single(value => value.Local == endpoint);
            if (!stored.Journal.Any(row => row.Digest == mapping.ProposalDigest && row.Operation == mapping.Operation
                && row.Status == OpStatus.Verified))
                throw Wire.Refuse(ErrorCode.Indeterminate, "A local creation endpoint requires its original separately verified creation receipt.");
        }
        return key;
    }

    private static Relationship CreationEdge(StoreSnapshot stored, Discovery schema, ItemKey created, CreationLink link)
    {
        var endpoint = Resolve(stored, link.Other);
        var kind = schema.Schema.Relations.Single(relation => relation.Reference == link.Relation);
        var from = link.Outgoing ? created : endpoint;
        var to = link.Outgoing ? endpoint : created;
        if (!kind.Directed && string.CompareOrdinal(from.NativeId, to.NativeId) > 0) (from, to) = (to, from);
        return new(from, to, link.Relation, RelationshipOrigin.Provider);
    }

    private static IEnumerable<ItemKey> Targets(Op op)
        => op.Links.Select(link => link.Other).Prepend(op.Item).Concat(op.Other is null ? [] : new[] { op.Other });

    private async Task<StoreSnapshot> Snapshot(CancellationToken ct)
    {
        var result = await _store.ReadAsync(_connection, ct);
        return result.IsSuccess ? result.Value : throw new AdoFailure(result.Error);
    }

    private async Task<StoreSnapshot> AcquireInputs(Bound bound, Discovery schema, Proposal proposal, StoreSnapshot stored, CancellationToken ct)
    {
        var keys = proposal.Operations.SelectMany(Targets)
            .Select(key => Resolve(stored, key)).Where(k => !k.IsLocal).Distinct().ToImmutableArray();
        return Mirror(stored, await Read(bound, schema, keys, ct));
    }

    private static ItemKey Resolve(StoreSnapshot stored, ItemKey key)
        => stored.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;

    private static StoreSnapshot Mirror(StoreSnapshot stored, QueryPage page)
    {
        var keys = page.Items.Select(i => i.Key).ToHashSet();
        return Copy(stored, stored.Version,
            items: stored.Items.Where(i => !keys.Contains(i.Key)).Concat(page.Items),
            relationships: page.Complete ? stored.Relationships.Where(r => r.Origin == RelationshipOrigin.Local
                || !keys.Contains(r.From) && !keys.Contains(r.To)).Concat(page.Relationships) : stored.Relationships);
    }

    private static void RefuseUnresolvedTargets(Proposal proposal, StoreSnapshot stored)
    {
        foreach (var row in stored.Journal.Where(j => j.Status is OpStatus.Applying or OpStatus.Applied or OpStatus.Partial or OpStatus.Indeterminate))
        {
            var prior = stored.Proposals.Single(p => p.Digest == row.Digest).Operations[row.Operation];
            if (proposal.Operations.Any(op => Targets(op).Intersect(Targets(prior)).Any()))
                throw Wire.Refuse(ErrorCode.Indeterminate,
                    "An unresolved effect fences this target, including replacement digests and comment/create identities; recover without replay.");
        }

    }

    private static void RefuseChangedActor(StoreSnapshot stored, Bound bound)
    {
        if (stored.Journal.SelectMany(j => j.Receipts).SelectMany(r => r.Evidence)
            .Any(e => e.StartsWith("actor:", StringComparison.Ordinal) && e != $"actor:{bound.PrincipalId}"))
            throw Wire.Refuse(ErrorCode.Conflict, "The stable identity binding no longer resolves the original native actor.");
    }

    private async Task<Receipt> PersistReceipt(Proposal proposal, Receipt receipt, QueryPage? observations)
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
        if (observations is not null) stored = Mirror(stored, observations);
        var next = Copy(stored, stored.Version + 1, journal: Replace(stored.Journal, recorded.Value), mappings: mappings);
        var result = await _store.CommitAsync(_connection, stored.Version, next, CancellationToken.None);
        return result.IsSuccess ? receipt : Undurable(result.Error);

        Receipt Undurable(Error error) => new(receipt.Digest, receipt.Operation, receipt.Attempt, OpStatus.Indeterminate,
            receipt.Evidence.Add($"Receipt durability failed: {error.Code}: {error.Message}; original durable fence remains."),
            receipt.Revision, receipt.PublishedKey, new(ErrorCode.StorageUnavailable, "Outcome could not be committed; no replay is authorized."));
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
        && a.Evidence.SequenceEqual(b.Evidence, StringComparer.Ordinal) && a.Error == b.Error;
}
