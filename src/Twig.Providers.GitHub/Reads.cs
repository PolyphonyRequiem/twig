using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.GitHub;

public sealed partial class GitHubProvider
{
    public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            var schema = await Discover(bound, token);
            return await Refresh(bound, schema, keys, token);
        }, ct));

    public Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            if (continuation is not null) throw Wire.Refuse(ErrorCode.Unsupported, "This bounded query does not accept a continuation.");
            var schema = await Discover(bound, token);
            CheckQuery(query, schema);
            var data = await Graph(bound,
                "query($id: ID!) { node(id: $id) { id ... on Repository { owner { id } issues(first: 100, orderBy: {field: CREATED_AT, direction: ASC}) { nodes { id } pageInfo { hasNextPage endCursor } } } } }",
                json => json.WriteString("id", _repository), token);
            var repo = Wire.Member(data, "node"); Repository(repo);
            var issues = Wire.Member(repo, "issues"); NoNext(issues);
            var keys = Wire.Member(issues, "nodes").EnumerateArray().Select(i => Key(Wire.Text(i, "id"))).ToImmutableArray();
            if (keys.Distinct().Count() != keys.Length) throw Wire.Invalid("Duplicate native query candidates.");
            var all = await Refresh(bound, schema, keys, token);
            var matched = new List<WorkItem>();
            foreach (var item in all.Items)
            {
                var match = Model.Matches(query, item);
                if (!match.IsSuccess) throw new GitHubFailure(match.Error);
                if (match.Value) matched.Add(item);
            }
            var selected = matched.Select(i => i.Key).ToHashSet();
            return new QueryPage(matched.ToImmutableArray(),
                all.Relationships.Where(r => selected.Contains(r.From) || selected.Contains(r.To)).ToImmutableArray(), null, true);
        }, ct));

    private static void CheckQuery(Query query, Discovery schema)
    {
        if (query.Kind == QueryKind.Equal && (!schema.Schema.Types.Any(t => t.Fields.Any(f =>
            f.Reference == query.Field && (query.Value!.Kind == FieldKind.Null || f.Kind == query.Value.Kind)))))
            throw Wire.Refuse(ErrorCode.Unsupported, "Query requires a discovered field and its exact typed value.");
        foreach (var child in query.Children) CheckQuery(child, schema);
    }

    private async Task<QueryPage> Refresh(Bound bound, Discovery schema, ImmutableArray<ItemKey> keys, CancellationToken ct)
    {
        var stored = await Snapshot(ct);
        RefuseChangedActor(stored, bound);
        var page = await Read(bound, schema, keys, ct);
        await bound.Session.ValidateAsync(ct);
        if (keys.IsEmpty) return page;
        var next = Mirror(stored, page, stored.Version + 1);
        var commit = await _store.CommitAsync(_connection, stored.Version, next, ct);
        if (!commit.IsSuccess) throw new GitHubFailure(commit.Error);
        return page with
        {
            Relationships = next.Relationships.Where(r => keys.Contains(r.From) || keys.Contains(r.To)).ToImmutableArray(),
        };
    }

    private async Task<QueryPage> Read(Bound bound, Discovery schema, ImmutableArray<ItemKey> keys, CancellationToken ct)
    {
        if (keys.Length > 100 || keys.Distinct().Count() != keys.Length)
            throw Wire.Refuse(ErrorCode.Unsupported, "Explicit reads require at most 100 distinct native keys.");
        var items = new List<WorkItem>();
        var edges = new HashSet<Relationship>();
        var perItem = new Dictionary<ItemKey, HashSet<Relationship>>();
        foreach (var key in keys)
        {
            var mapped = await ReadItem(bound, schema, key, ct);
            items.Add(mapped.Item); edges.UnionWith(mapped.Edges); perItem.Add(key, mapped.Edges);
        }
        foreach (var edge in edges)
            foreach (var key in new[] { edge.From, edge.To }.Where(perItem.ContainsKey))
                if (!perItem[key].Contains(edge)) throw Wire.Invalid("Contradictory acquired native relationship endpoints.");
        return new(items.ToImmutableArray(), edges.OrderBy(e => OrderKey(e.From), StringComparer.Ordinal)
            .ThenBy(e => OrderKey(e.To), StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ToImmutableArray(), null, true);
    }

    private async Task<(WorkItem Item, HashSet<Relationship> Edges)> ReadItem(Bound bound, Discovery schema, ItemKey key, CancellationToken ct)
    {
        var id = Id(key);
        var selection = "id title body state updatedAt repository { id owner { id } }";
        if (schema.Fields.Contains("issueType")) selection += " issueType { id }";
        if (schema.Fields.Contains("milestone")) selection += " milestone { id }";
        if (schema.NativeRelations.Contains(Hierarchy)) selection += " parent { id repository { id owner { id } } }";
        var document = $"query($id: ID!) {{ node(id: $id) {{ __typename ... on Issue {{ {selection} }} }} }}";
        var data = await Graph(bound, document,
            json => json.WriteString("id", id), ct);
        var node = Wire.Member(data, "node");
        if (Wire.Text(node, "__typename") != "Issue" || Wire.Text(node, "id") != id)
            throw Wire.Invalid("Native response did not identify the requested issue.");
        Repository(Wire.Member(node, "repository"));
        var timestamp = Wire.Text(node, "updatedAt");
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated))
            throw Wire.Invalid("Native issue timestamp is unavailable.");
        var fields = FieldValues.Empty.Set(Title, FieldValue.String(Wire.Text(node, "title")))
            .Set(StateField, FieldValue.String(Wire.Text(node, "state")))
            .Set("github.updatedAt", FieldValue.At(updated));
        var body = Wire.Member(node, "body");
        fields = fields.Set(Body, body.ValueKind == JsonValueKind.Null ? FieldValue.Null : FieldValue.RichText(body.GetString()!, FieldKind.Markdown));
        var type = Untyped;
        if (schema.Fields.Contains("issueType") && Wire.Member(node, "issueType").ValueKind != JsonValueKind.Null)
            type = Wire.Id(Wire.Text(Wire.Member(node, "issueType"), "id"));
        if (!schema.Schema.Types.Any(t => t.Reference == type))
            throw Wire.Invalid("Item type was not observed in the native type catalog.");
        if (schema.Fields.Contains("milestone"))
        {
            var milestone = Wire.Member(node, "milestone");
            fields = fields.Set(Milestone, milestone.ValueKind == JsonValueKind.Null ? FieldValue.Null : FieldValue.Identity(Wire.Id(Wire.Text(milestone, "id"))));
        }
        foreach (var (native, field) in new[] { ("labels", Labels), ("assignees", Assignees) }.Where(p => schema.Fields.Contains(p.Item1)))
        {
            var values = await ConnectionNodes(bound, id, native, "id", ct);
            fields = fields.Set(field, FieldValue.Strings(values.Select(v => Wire.Id(Wire.Text(v, "id"))).Order(StringComparer.Ordinal)));
        }
        var edges = new HashSet<Relationship>();
        if (schema.NativeRelations.Contains(Hierarchy))
        {
            var parent = Wire.Member(node, "parent");
            if (parent.ValueKind != JsonValueKind.Null) edges.Add(new(RelatedKey(parent), key, Hierarchy, RelationshipOrigin.Provider));
            foreach (var child in await ConnectionNodes(bound, id, "subIssues", "id repository { id owner { id } }", ct))
                edges.Add(new(key, RelatedKey(child), Hierarchy, RelationshipOrigin.Provider));
        }
        if (schema.NativeRelations.Contains(Dependency))
        {
            foreach (var blocker in await ConnectionNodes(bound, id, "blockedBy", "id repository { id owner { id } }", ct))
                edges.Add(new(RelatedKey(blocker), key, Dependency, RelationshipOrigin.Provider));
            foreach (var blocked in await ConnectionNodes(bound, id, "blocking", "id repository { id owner { id } }", ct))
                edges.Add(new(key, RelatedKey(blocked), Dependency, RelationshipOrigin.Provider));
        }
        if (edges.Any(e => e.From == e.To)) throw Wire.Invalid("Native relationship has identical endpoints.");
        var check = await Graph(bound, document, json => json.WriteString("id", id), ct);
        var after = Wire.Member(check, "node");
        if (!JsonElement.DeepEquals(after, node))
            throw Wire.Refuse(ErrorCode.Conflict, "Issue changed during multi-request observation; no coherent acquisition is claimed.");
        var observation = Wire.Json(json =>
        {
            json.WriteStartObject(); json.WriteString("id", id); json.WriteString("type", type);
            json.WriteString("updatedAt", timestamp); json.WriteStartObject("fields");
            foreach (var (name, value) in fields)
            {
                json.WritePropertyName(name); json.WriteStartArray(); json.WriteNumberValue((int)value.Kind);
                if (value.Kind == FieldKind.Instant) json.WriteStringValue(value.Instant.ToString("O", CultureInfo.InvariantCulture));
                else if (value.Kind == FieldKind.StringList) foreach (var entry in value.Values) json.WriteStringValue(entry);
                else json.WriteStringValue(value.Text);
                json.WriteEndArray();
            }
            json.WriteEndObject(); json.WriteStartArray("edges");
            foreach (var edge in edges.OrderBy(e => OrderKey(e.From), StringComparer.Ordinal)
                .ThenBy(e => OrderKey(e.To), StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal))
            {
                json.WriteStartArray(); json.WriteStringValue(edge.From.NativeId); json.WriteStringValue(edge.To.NativeId);
                json.WriteStringValue(edge.Kind); json.WriteEndArray();
            }
            json.WriteEndArray(); json.WriteEndObject();
        });
        return (new(key, type, fields, new(key, "github-observation-v1:" + Wire.Hash(observation)), true), edges);
    }

    private ItemKey RelatedKey(JsonElement node)
    {
        Repository(Wire.Member(node, "repository"));
        return Key(Wire.Text(node, "id"));
    }

    private async Task<JsonElement[]> ConnectionNodes(Bound bound, string id, string field, string selection, CancellationToken ct)
    {
        var data = await Graph(bound,
            $"query($id: ID!) {{ node(id: $id) {{ __typename id ... on Issue {{ {field}(first: 100) {{ nodes {{ {selection} }} pageInfo {{ hasNextPage endCursor }} }} }} }} }}",
            json => json.WriteString("id", id), ct);
        var node = Wire.Member(data, "node");
        if (Wire.Text(node, "__typename") != "Issue" || Wire.Text(node, "id") != id) throw Wire.Invalid("Connection target changed.");
        var connection = Wire.Member(node, field); NoNext(connection);
        var nodes = Wire.Member(connection, "nodes").EnumerateArray().ToArray();
        if (nodes.Select(n => Wire.Id(Wire.Text(n, "id"))).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw Wire.Invalid("Duplicate native connection identities.");
        return nodes;
    }
}
