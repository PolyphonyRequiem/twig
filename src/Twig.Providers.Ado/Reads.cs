using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Globalization;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<QueryPage>> ReadAsync(Connection connection, ImmutableArray<ItemKey> keys, CancellationToken ct, bool captureOnly = false)
        => Guard(() => WithSession(connection, async (bound, token) =>
            await Read(bound, await Discover(bound, token), keys, token), ct));

    public Task<Result<QueryPage>> QueryAsync(Connection connection, Query query, string? continuation, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            var schema = await Discover(bound, token);
            CheckQuery(query, schema);
            var binding = Convert.ToHexStringLower(SHA256.HashData(Wire.Json(json =>
            {
                json.WriteStartArray();
                json.WriteStringValue(_connection.Provider.Value);
                json.WriteStringValue(_connection.ResourceScope);
                json.WriteStringValue(_connection.Destination);
                json.WriteStringValue(_connection.IdentityRef);
                json.WriteStringValue(bound.PrincipalId);
                json.WriteStringValue(schema.Schema.Fingerprint);
                json.WriteBase64StringValue(Model.QueryBytes(query));
                json.WriteEndArray();
            })));
            var after = 0;
            if (continuation is not null)
            {
                try
                {
                    if (continuation.Length > 4096) throw new JsonException("Cursor exceeds its bound.");
                    var decoded = Convert.FromBase64String(continuation);
                    using var document = JsonDocument.Parse(decoded);
                    var cursor = document.RootElement;
                    Wire.Unique(cursor);
                    if (cursor.ValueKind != JsonValueKind.Object || cursor.EnumerateObject().Count() != 3
                        || cursor.GetProperty("version").GetInt32() != 1 || cursor.GetProperty("binding").GetString() != binding)
                        throw new JsonException("Cursor belongs to another connection, actor, schema or query.");
                    after = cursor.GetProperty("after").GetInt32();
                    if (after <= 0) throw new JsonException("Cursor requires a positive consumed native ID.");
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
                {
                    throw Wire.Refuse(ErrorCode.InvalidInput, $"Invalid bound query continuation: {ex.Message}");
                }
            }
            // WIQL string collation/null/NOT semantics differ from the ordinal typed Core AST.
            // Acquire a bounded superset and evaluate exactly; never claim an approximate filter is equivalent.
            var body = Wire.Json(json =>
            {
                json.WriteStartObject();
                json.WriteString("query", "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project"
                    + (after == 0 ? "" : " AND [System.Id] > " + after.ToString(CultureInfo.InvariantCulture))
                    + " ORDER BY [System.Id]");
                json.WriteEndObject();
            });
            var response = await Request(bound, HttpMethod.Post,
                $"{bound.Project}/_apis/wit/wiql?$top=201&api-version={Api}", body, token);
            if (Wire.Text(response, "queryType") != "flat") throw Wire.Invalid("Expected a complete flat WIQL result.");
            var ids = response.GetProperty("workItems").EnumerateArray().Select(i => Wire.Positive(i, "id")).ToArray();
            if (ids.Length > 201 || ids.Any(id => id <= after) || !ids.SequenceEqual(ids.Distinct().Order()))
                throw Wire.Invalid("Native keyset page is oversized, duplicated or not strictly ordered after its consumed ID.");
            var keys = ids.Take(200).Select(Key).ToImmutableArray();
            var page = await Read(bound, schema, keys, token);
            var items = page.Items.Where(item =>
            {
                var matched = Model.Matches(query, item);
                if (!matched.IsSuccess) throw new AdoFailure(matched.Error);
                return matched.Value;
            }).ToImmutableArray();
            var selected = items.Select(i => i.Key).ToHashSet();
            return page with
            {
                Items = items,
                Relationships = page.Relationships.Where(r => selected.Contains(r.From) || selected.Contains(r.To)).ToImmutableArray(),
                Complete = ids.Length <= 200,
                Continuation = ids.Length <= 200 ? null : Convert.ToBase64String(Wire.Json(json =>
                {
                    json.WriteStartObject(); json.WriteNumber("version", 1); json.WriteString("binding", binding);
                    json.WriteNumber("after", ids[199]); json.WriteEndObject();
                })),
            };
        }, ct));

    private static void CheckQuery(Query query, Discovery schema, int depth = 0)
    {
        if (depth > 32) throw Wire.Refuse(ErrorCode.Unsupported, "Query nesting exceeds the adapter bound.");
        if (query.Kind is QueryKind.Equal or QueryKind.Contains or QueryKind.Under or QueryKind.GreaterOrEqual
            && (!schema.Fields.TryGetValue(query.Field!, out var field)
            || query.Value!.Kind != FieldKind.Null && query.Value.Kind != field.Kind || query.Value!.Kind == FieldKind.Raw))
            throw Wire.Refuse(ErrorCode.Unsupported, "Query field/value is not an exactly comparable discovered field.");
        foreach (var child in query.Children) CheckQuery(child, schema, depth + 1);
    }

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
        var resources = ImmutableArray.CreateBuilder<ResourceLink>();
        if (root.TryGetProperty("relations", out var relations))
            foreach (var relation in relations.EnumerateArray())
            {
                var name = Wire.Text(relation, "rel");
                if (!schema.Relations.TryGetValue(name, out var kind))
                {
                    if (schema.NonItemRelations.Contains(name))
                    {
                        string? Attribute(string attribute)
                        {
                            if (!relation.TryGetProperty("attributes", out var attributes)
                                || !attributes.TryGetProperty(attribute, out var value) || value.ValueKind == JsonValueKind.Null)
                                return null;
                            return value.GetString();
                        }
                        resources.Add(new(name, Wire.Text(relation, "url"), Attribute("name"), Attribute("comment")));
                        continue;
                    }
                    throw Wire.Invalid("An incident relation has unknown native semantics.");
                }
                var other = RelationshipTarget(Wire.Text(relation, "url"), bound);
                var from = kind.Forward ? key : other;
                var to = kind.Forward ? other : key;
                if (!kind.Directed && string.CompareOrdinal(from.NativeId, to.NativeId) > 0) (from, to) = (to, from);
                edges.Add(new(from, to, kind.Canonical, RelationshipOrigin.Provider));
            }
        if (edges.Distinct().Count() != edges.Count) throw Wire.Invalid("Duplicate native relationship observations.");
        return (new(key, type, new(values), revision, true, resources, true), edges.ToImmutable());
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
