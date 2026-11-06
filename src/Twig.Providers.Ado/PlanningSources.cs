using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    public Task<Result<PlanningSources>> DiscoverPlanningSourcesAsync(Connection connection, string? team, CancellationToken ct)
        => Guard(() => WithSession(connection, (bound, token) => DiscoverPlanningSources(bound, team, token), ct));

    public Task<Result<PlanningBrowsePage>> BrowsePlanningAsync(Connection connection, string? team,
        PlanningSourceKind kind, string id, string? expectedTeam, CancellationToken ct)
        => BrowsePlanning(connection, team, kind, id, expectedTeam, null, ct);

    public Task<Result<PlanningBrowsePage>> BrowsePlanningAsync(Connection connection, string? team,
        PlanningSourceKind kind, string id, string? expectedTeam, ProcessSchema mappingSchema, CancellationToken ct)
        => mappingSchema is null || mappingSchema.Connection != connection
            ? Task.FromResult(Result<PlanningBrowsePage>.Fail(ErrorCode.Conflict, "The recorded native mapping belongs to another connection or is missing."))
            : BrowsePlanning(connection, team, kind, id, expectedTeam, mappingSchema, ct);

    private Task<Result<PlanningBrowsePage>> BrowsePlanning(Connection connection, string? team,
        PlanningSourceKind kind, string id, string? expectedTeam, ProcessSchema? mappingSchema, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) =>
        {
            if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(id) || id.Length > 256)
                throw Wire.Refuse(ErrorCode.InvalidInput, "An exact native saved-query or backlog identity is required.");
            var canonicalTeam = await ResolvePlanningTeam(bound, team, token);
            if (canonicalTeam != expectedTeam)
                throw Wire.Refuse(ErrorCode.Conflict, "The selected canonical native team changed; no query or backlog items were acquired.");
            if (kind == PlanningSourceKind.SavedQuery && Wire.GuidText(id) != id)
                throw Wire.Refuse(ErrorCode.InvalidInput, "An exact canonical native saved-query ID is required.");
            if (kind == PlanningSourceKind.Backlog && canonicalTeam is null)
                throw Wire.Refuse(ErrorCode.Unavailable, "A native backlog requires an explicitly selected team.");
            var metadataRoute = kind == PlanningSourceKind.SavedQuery
                ? $"{bound.Project}/_apis/wit/queries/{Wire.Escape(id)}?$expand=all&api-version={Api}"
                : $"{bound.Project}/{canonicalTeam}/_apis/work/backlogs/{Wire.Escape(id)}?api-version={Api}";
            var metadata = await Get(bound, metadataRoute, token);
            var source = kind == PlanningSourceKind.SavedQuery ? QuerySource(metadata, "")
                : new PlanningSource(kind, Wire.Text(metadata, "id"), Wire.Text(metadata, "name"), Wire.Text(metadata, "name"), RequiresTeam: true);
            if (source.Id != id || source.Kind != kind || source.IsFolder)
                throw Wire.Refuse(ErrorCode.Conflict, "The direct native source lookup returned another identity or a folder; no source was executed.");
            if (source.RequiresTeam && canonicalTeam is null)
                throw Wire.Refuse(ErrorCode.Unavailable, "This native query or backlog requires an explicitly selected team; no default team was substituted.");
            var scope = bound.Project + (canonicalTeam is null ? "" : "/" + Wire.Escape(canonicalTeam));
            var route = kind == PlanningSourceKind.SavedQuery
                ? $"{scope}/_apis/wit/wiql/{Wire.Escape(source.Id)}?api-version={Api}"
                : $"{scope}/_apis/work/backlogs/{Wire.Escape(source.Id)}/workItems?api-version={Api}";
            var result = await Get(bound, route, token);
            var observedAt = kind == PlanningSourceKind.SavedQuery ? result.GetProperty("asOf").GetDateTimeOffset() : DateTimeOffset.UtcNow;
            var keys = new List<ItemKey>();
            if (kind == PlanningSourceKind.SavedQuery && Wire.Text(result, "queryType") == "flat")
            {
                foreach (var item in result.GetProperty("workItems").EnumerateArray()) keys.Add(Key(Wire.Positive(item, "id")));
                if (keys.Distinct().Count() != keys.Count) throw Wire.Invalid("The flat native query returned duplicate identities.");
            }
            else
            {
                if (kind == PlanningSourceKind.SavedQuery && Wire.Text(result, "queryType") is not ("tree" or "oneHop"))
                    throw Wire.Refuse(ErrorCode.Unsupported, "The native query result shape is unsupported; no flat membership was fabricated.");
                var name = kind == PlanningSourceKind.SavedQuery ? "workItemRelations" : "workItems";
                foreach (var link in result.GetProperty(name).EnumerateArray())
                {
                    var endpoints = 0;
                    foreach (var end in new[] { "source", "target" })
                        if (link.TryGetProperty(end, out var item) && item.ValueKind != JsonValueKind.Null)
                        { keys.Add(Key(Wire.Positive(item, "id"))); endpoints++; }
                    if (endpoints == 0) throw Wire.Invalid("The native planning membership contains an empty link.");
                }
            }
            var distinct = keys.Distinct().ToImmutableArray();
            if (distinct.Length > 10000)
                throw Wire.Refuse(ErrorCode.Partial, "The native planning result exceeds the bounded item acquisition limit; no truncated result was mirrored.");
            // Link-result rows establish membership only. Hierarchy comes from each item's native incident observations.
            var mapping = mappingSchema is null ? await Discover(bound, token) : await PlanningMapping(bound, mappingSchema, token);
            var page = await ReadPlanningItems(bound, mapping, distinct, token);
            return new PlanningBrowsePage(source, page, observedAt, canonicalTeam);
        }, ct));

    private async Task<Discovery> PlanningMapping(Bound bound, ProcessSchema schema, CancellationToken ct)
    {
        if (schema.Connection != bound.Connection || schema.Types.IsEmpty || schema.Types.Length > 256)
            throw Wire.Refuse(ErrorCode.Conflict, "Recorded native mapping metadata is missing or belongs to another connection; refresh process metadata explicitly.");
        var names = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var type in schema.Types)
        {
            var candidates = type.Rules.Where(rule => rule.Disabled).SelectMany(rule => rule.Values)
                .Where(value => value.Kind == FieldKind.Raw && value.DataType == "ado.native-type-name").ToArray();
            if (candidates.Length != 1 || string.IsNullOrWhiteSpace(candidates[0].Text)
                || names.Values.Contains(candidates[0].Text, StringComparer.Ordinal))
                throw Wire.Refuse(ErrorCode.Unavailable, "A recorded item type has no unique native name mapping; refresh process metadata explicitly.");
            names.Add(type.Reference, candidates[0].Text!);
        }
        var (relations, nonItems, _) = await DiscoverRelations(bound, ct);
        var known = relations.Where(pair => schema.Relations.Any(kind => kind.Reference == pair.Value.Canonical
            && kind.Shape == pair.Value.Shape && kind.Directed == pair.Value.Directed)).ToImmutableDictionary(StringComparer.Ordinal);
        return new(schema, names.ToImmutable(), known, ImmutableDictionary<string, FieldDef>.Empty, nonItems);
    }

    private async Task<QueryPage> ReadPlanningItems(Bound bound, Discovery schema, ImmutableArray<ItemKey> keys, CancellationToken ct)
    {
        var items = new List<WorkItem>();
        var observations = new Dictionary<ItemKey, HashSet<Relationship>>();
        foreach (var batch in keys.Chunk(200))
        {
            var expected = batch.ToHashSet();
            var body = Wire.Json(json =>
            {
                json.WriteStartObject(); json.WriteStartArray("ids");
                foreach (var key in batch) json.WriteNumberValue(Id(key));
                json.WriteEndArray(); json.WriteString("$expand", "relations"); json.WriteString("errorPolicy", "fail"); json.WriteEndObject();
            });
            var rows = Wire.Rows(await Request(bound, HttpMethod.Post, $"{bound.Project}/_apis/wit/workitemsbatch?api-version={Api}", body, ct));
            if (rows.Length != batch.Length) throw Wire.Invalid("The native batch omitted requested items; no incomplete planning membership was returned.");
            foreach (var row in rows)
            {
                var key = Key(Wire.Positive(row, "id"));
                if (!expected.Remove(key)) throw Wire.Invalid("The native batch duplicated or substituted a requested identity.");
                var (item, edges) = MapItem(row, bound, schema, key);
                items.Add(item); observations.Add(key, edges.ToHashSet());
            }
        }
        return CompleteRead(items, observations);
    }

    private async Task<PlanningSources> DiscoverPlanningSources(Bound bound, string? team, CancellationToken ct)
    {
        var canonicalTeam = await ResolvePlanningTeam(bound, team, ct);
        var entries = ImmutableArray.CreateBuilder<PlanningSource>();
        var roots = Wire.Rows(await Get(bound, $"{bound.Project}/_apis/wit/queries?$depth=2&$expand=all&api-version={Api}", ct));
        var pending = new Stack<(JsonElement Node, string Parent, int Depth)>(roots.Reverse().Select(node => (node, "", 0)));
        var identities = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var entry))
        {
            if (entries.Count >= 2048 || entry.Depth > 32)
                throw Wire.Refuse(ErrorCode.Partial, "The native saved-query hierarchy exceeds its discovery bound.");
            var node = entry.Node;
            var id = Wire.GuidText(Wire.Text(node, "id"));
            if (!identities.Add(id)) throw Wire.Invalid("The native saved-query hierarchy repeats an identity or contains a cycle.");
            var source = QuerySource(node, entry.Parent);
            var path = source.Path;
            var folder = source.IsFolder;
            entries.Add(source);
            var children = node.TryGetProperty("children", out var values) ? values.EnumerateArray().ToArray() : [];
            var hasChildren = node.TryGetProperty("hasChildren", out var hasValue) && hasValue.GetBoolean();
            if (!folder && (hasChildren || children.Length != 0)) throw Wire.Invalid("A native saved query unexpectedly contains folder children.");
            if (folder && hasChildren && children.Length == 0)
            {
                node = await Get(bound, $"{bound.Project}/_apis/wit/queries/{id}?$depth=1&$expand=all&api-version={Api}", ct);
                if (Wire.GuidText(Wire.Text(node, "id")) != id)
                    throw Wire.Refuse(ErrorCode.Conflict, "The native query folder lookup resolved a different identity.");
                children = node.TryGetProperty("children", out values) ? values.EnumerateArray().ToArray() : [];
                if (children.Length == 0 && node.TryGetProperty("hasChildren", out var remaining) && remaining.GetBoolean())
                    throw Wire.Refuse(ErrorCode.Partial, "The native query folder was not exhausted; no complete hierarchy was claimed.");
            }
            foreach (var child in children.Reverse()) pending.Push((child, path, entry.Depth + 1));
        }
        var gaps = ImmutableArray<string>.Empty;
        if (canonicalTeam is not null)
        {
            var backlogs = Wire.Rows(await Get(bound, $"{bound.Project}/{canonicalTeam}/_apis/work/backlogs?api-version={Api}", ct));
            if (backlogs.Length > 64) throw Wire.Refuse(ErrorCode.Partial, "The native backlog roster exceeds its discovery bound.");
            foreach (var row in backlogs)
            {
                var id = Wire.Text(row, "id");
                if (id.Length > 256 || !identities.Add("backlog:" + id)) throw Wire.Invalid("The native backlog roster has an invalid or repeated identity.");
                var name = Wire.Text(row, "name");
                entries.Add(new(PlanningSourceKind.Backlog, id, name, name, RequiresTeam: true));
            }
        }
        else gaps = ["No native team is selected; team backlogs and team-dependent saved queries are unavailable."];
        return new(canonicalTeam, entries.ToImmutable(), gaps);
    }

    private static PlanningSource QuerySource(JsonElement node, string parent)
    {
        var id = Wire.GuidText(Wire.Text(node, "id"));
        var name = Wire.Text(node, "name");
        var path = node.TryGetProperty("path", out var pathValue) && pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString()! : parent.Length == 0 ? name : parent + "/" + name;
        var folder = node.TryGetProperty("isFolder", out var folderValue) && folderValue.GetBoolean();
        string? Text(string property) => node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var wiql = Text("wiql");
        return new(PlanningSourceKind.SavedQuery, id, name, path, folder, Text("queryType"),
            !folder && (wiql is null || wiql.Contains("@CurrentIteration", StringComparison.OrdinalIgnoreCase)
                || wiql.Contains("@TeamAreas", StringComparison.OrdinalIgnoreCase)));
    }
}
