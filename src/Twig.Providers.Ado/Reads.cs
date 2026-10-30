using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
            await Read(bound, await Discover(bound, token), keys, token), ct));

    public Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            if (continuation is not null) throw Wire.Refuse(ErrorCode.Unsupported, "ADO query continuation is not implemented.");
            var schema = await Discover(bound, token);
            CheckQuery(query, schema);
            // WIQL string collation/null/NOT semantics differ from the ordinal typed Core AST.
            // Acquire a bounded superset and evaluate exactly; never claim an approximate filter is equivalent.
            var body = Wire.Json(json =>
            {
                json.WriteStartObject();
                json.WriteString("query", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.Id]");
                json.WriteEndObject();
            });
            var response = await Request(bound, HttpMethod.Post,
                $"{bound.Project}/_apis/wit/wiql?$top=201&api-version={Api}", body, token);
            if (Wire.Text(response, "queryType") != "flat") throw Wire.Invalid("Expected a complete flat WIQL result.");
            var keys = response.GetProperty("workItems").EnumerateArray().Select(i => Key(Wire.Positive(i, "id"))).ToImmutableArray();
            if (keys.Length > 200) throw Wire.Refuse(ErrorCode.Unsupported,
                "Exact Core query evaluation is bounded to 200 native project items; this shape requires a larger acquisition.");
            var page = await Read(bound, schema, keys, token);
            var items = page.Items.Where(item => Matches(query, item.Fields)).ToImmutableArray();
            var selected = items.Select(i => i.Key).ToHashSet();
            return page with
            {
                Items = items,
                Relationships = page.Relationships.Where(r => selected.Contains(r.From) || selected.Contains(r.To)).ToImmutableArray(),
            };
        }, ct));

    private static void CheckQuery(Query query, Discovery schema, int depth = 0)
    {
        if (depth > 32) throw Wire.Refuse(ErrorCode.Unsupported, "Query nesting exceeds the adapter bound.");
        if (query.Kind == QueryKind.Equal && (!schema.Fields.TryGetValue(query.Field!, out var field)
            || query.Value!.Kind != FieldKind.Null && query.Value.Kind != field.Kind || query.Value!.Kind == FieldKind.Raw))
            throw Wire.Refuse(ErrorCode.Unsupported, "Query field/value is not an exactly comparable discovered field.");
        foreach (var child in query.Children) CheckQuery(child, schema, depth + 1);
    }

    private static bool Matches(Query query, FieldValues fields) => query.Kind switch
    {
        QueryKind.All => true,
        QueryKind.Equal => fields.TryGetValue(query.Field!, out var value)
            ? value.Equals(query.Value) : throw Wire.Invalid("Query field was not observed on a candidate type."),
        QueryKind.And => query.Children.Select(child => Matches(child, fields)).ToArray().All(value => value),
        QueryKind.Or => query.Children.Select(child => Matches(child, fields)).ToArray().Any(value => value),
        QueryKind.Not => !Matches(query.Children[0], fields),
        _ => throw Wire.Refuse(ErrorCode.Unsupported, "Unknown query operator."),
    };

    private async Task<QueryPage> Read(Bound bound, Discovery schema, ImmutableArray<ItemKey> keys, CancellationToken ct)
    {
        if (keys.IsDefault || keys.Length > 200 || keys.Distinct().Count() != keys.Length)
            throw Wire.Refuse(ErrorCode.InvalidInput, "Read requires at most 200 distinct explicit identities.");
        var items = new List<WorkItem>();
        var observations = new Dictionary<ItemKey, HashSet<Relationship>>();
        foreach (var key in keys)
        {
            var id = Id(key);
            var root = await Get(bound, $"{bound.Project}/_apis/wit/workitems/{id}?$expand=relations&api-version={Api}", ct);
            var (item, edges) = MapItem(root, bound, schema, key);
            items.Add(item);
            observations.Add(key, edges.ToHashSet());
        }
        var relationships = observations.Values.SelectMany(e => e).Distinct().ToImmutableArray();
        foreach (var edge in relationships)
            foreach (var endpoint in new[] { edge.From, edge.To }.Distinct())
                if (observations.TryGetValue(endpoint, out var observed) && !observed.Contains(edge))
                    throw Wire.Invalid("Acquired relationship endpoints disagree; incident completeness cannot be certified.");
        return new(items.ToImmutableArray(), relationships, null, true);
    }

    private (WorkItem Item, ImmutableArray<Relationship> Edges) MapItem(JsonElement root, Bound bound, Discovery schema, ItemKey key)
    {
        if (Key(Wire.Positive(root, "id")) != key) throw Wire.Invalid("Native item response targets a different identity.");
        var revision = Revision(key, Wire.Positive(root, "rev"));
        var fields = root.GetProperty("fields");
        if (Wire.Text(fields, "System.TeamProject") != bound.ProjectName)
            throw Wire.Refuse(ErrorCode.Conflict, "Item is not in the immutable destination project.");
        var nativeType = Wire.Text(fields, "System.WorkItemType");
        var type = schema.TypeNames.SingleOrDefault(p => p.Value == nativeType).Key
            ?? throw Wire.Invalid("Item type has no unique process reference.");
        var definition = schema.Schema.Types.Single(t => t.Reference == type);
        var values = new List<KeyValuePair<string, FieldValue>>();
        foreach (var field in definition.Fields)
            values.Add(KeyValuePair.Create(field.Reference, fields.TryGetProperty(field.Reference, out var value)
                ? Wire.Value(value, field.Kind) : FieldValue.Null));
        foreach (var field in fields.EnumerateObject().Where(f => !definition.Fields.Any(d => d.Reference == f.Name)))
            values.Add(KeyValuePair.Create(field.Name, FieldValue.Raw(field.Value.GetRawText(), "undeclared-ado-field")));
        var edges = ImmutableArray.CreateBuilder<Relationship>();
        if (root.TryGetProperty("relations", out var relations))
            foreach (var relation in relations.EnumerateArray())
            {
                var name = Wire.Text(relation, "rel");
                if (!schema.Relations.TryGetValue(name, out var kind))
                {
                    if (schema.NonItemRelations.Contains(name)) continue;
                    throw Wire.Invalid("An incident relation has unknown native semantics.");
                }
                var other = RelationshipTarget(Wire.Text(relation, "url"), bound);
                var from = kind.Forward ? key : other;
                var to = kind.Forward ? other : key;
                if (!kind.Directed && string.CompareOrdinal(from.NativeId, to.NativeId) > 0) (from, to) = (to, from);
                edges.Add(new(from, to, kind.Canonical, RelationshipOrigin.Provider));
            }
        if (edges.Distinct().Count() != edges.Count) throw Wire.Invalid("Duplicate native relationship observations.");
        return (new(key, type, new(values), revision, true), edges.ToImmutable());
    }

    private ItemKey RelationshipTarget(string value, Bound bound)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) throw Wire.Invalid("Native relationship URL is invalid.");
        var prefix = bound.OrganizationUrl.AbsolutePath.TrimEnd('/') + "/_apis/wit/workItems/";
        if (uri.Scheme != bound.OrganizationUrl.Scheme || uri.Authority != bound.OrganizationUrl.Authority
            || !uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !int.TryParse(uri.AbsolutePath[prefix.Length..], out var id) || id <= 0)
            throw Wire.Refuse(ErrorCode.Unsupported, "Cross-collection or noncanonical work item relationships are not supported.");
        return Key(id);
    }
}
