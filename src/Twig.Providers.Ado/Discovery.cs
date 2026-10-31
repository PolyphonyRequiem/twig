using System.Collections.Immutable;
using System.Text.Json;
using Twig.Core;

namespace Twig.Providers.Ado;

public sealed partial class AdoProvider
{
    private sealed record NativeRelation(string Reference, string Canonical, bool Forward, RelationShape Shape, bool Directed);
    private sealed record Discovery(ProcessSchema Schema, ImmutableDictionary<string, string> TypeNames,
        ImmutableDictionary<string, NativeRelation> Relations, ImmutableDictionary<string, FieldDef> Fields,
        ImmutableHashSet<string> NonItemRelations);

    public Task<Result<ProcessSchema>> DiscoverSchemaAsync(Connection connection, CancellationToken ct)
        => Guard(() => WithSession(connection, async (bound, token) => (await Discover(bound, token)).Schema, ct));

    private async Task<Discovery> Discover(Bound bound, CancellationToken ct)
    {
        var gaps = new List<string>();
        var fields = Wire.Rows(await Get(bound, $"_apis/wit/fields?api-version={Api}", ct))
            .ToDictionary(f => Wire.Text(f, "referenceName"), StringComparer.OrdinalIgnoreCase);
        var relationRows = Wire.Rows(await Get(bound, $"_apis/wit/workitemrelationtypes?api-version={Api}", ct));
        var relations = new Dictionary<string, NativeRelation>(StringComparer.Ordinal);
        var nonItems = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var resources = new List<RelationKind>();
        foreach (var row in relationRows)
        {
            var attributes = row.GetProperty("attributes");
            var usage = Wire.Text(attributes, "usage");
            if (usage == "resourceLink")
            {
                nonItems.Add(Wire.Text(row, "referenceName"));
                var name = Wire.Text(row, "referenceName");
                if (name is "ArtifactLink" or "Hyperlink"
                    && attributes.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean())
                    resources.Add(new(name, RelationShape.Resource, false));
                continue;
            }
            if (usage != "workItemLink") throw Wire.Invalid("Unknown relation usage cannot certify incident completeness.");
            if (!Wire.Flag(attributes, "enabled")) continue;
            var reference = Wire.Text(row, "referenceName");
            var directed = Wire.Flag(attributes, "directional");
            var forward = !directed || Wire.Flag(attributes, "isForward");
            var canonical = forward ? reference : Wire.Text(attributes, "oppositeEndReferenceName");
            var shape = Wire.Text(attributes, "topology") switch
            {
                "tree" => RelationShape.Hierarchy,
                "dependency" => RelationShape.Dependency,
                "network" => RelationShape.Association,
                _ => throw Wire.Invalid("Unknown native relation topology."),
            };
            relations.Add(reference, new(reference, canonical, forward, shape, directed));
        }
        if (relations.Values.Any(r => !relations.TryGetValue(r.Canonical, out var other) || !other.Forward
            || other.Shape != r.Shape || other.Directed != r.Directed))
            throw Wire.Invalid("Native relationship endpoints disagree.");
        var transitions = Wire.Rows(await Get(bound,
            $"{bound.Project}/_apis/wit/workitemtypes?$expand=all&api-version={Api}", ct))
            .ToDictionary(t => Wire.Text(t, "referenceName"), StringComparer.OrdinalIgnoreCase);
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in Wire.Rows(await Get(bound, $"{bound.Project}/_apis/wit/workitemtypecategories?api-version={Api}", ct)))
            if (Wire.Text(category, "referenceName") == "Microsoft.HiddenCategory")
                foreach (var member in category.GetProperty("workItemTypes").EnumerateArray()) hidden.Add(Wire.Text(member, "name"));
        var types = new List<ItemType>();
        var names = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var definitions = ImmutableDictionary.CreateBuilder<string, FieldDef>(StringComparer.Ordinal);
        var processRoute = $"_apis/work/processes/{Wire.Escape(bound.ProcessId)}/workItemTypes";
        foreach (var row in Wire.Rows(await Get(bound, $"{processRoute}?api-version=7.1-preview.2", ct)))
        {
            var reference = Wire.Text(row, "referenceName");
            var name = Wire.Text(row, "name");
            names.Add(reference, name);
            var route = $"{processRoute}/{Wire.Escape(reference)}";
            var typeFields = new List<FieldDef>();
            var rules = new List<FieldRule>();
            foreach (var field in Wire.Rows(await Get(bound, $"{route}/fields?api-version=7.1-preview.2", ct)))
            {
                var fieldRef = Wire.Text(field, "referenceName");
                if (!fields.TryGetValue(fieldRef, out var global)) throw Wire.Invalid("Type field lacks its native definition.");
                var canonical = Wire.Text(global, "referenceName");
                var kind = Wire.Kind(global);
                var definition = new FieldDef(canonical, kind, Wire.Flag(global, "readOnly"));
                typeFields.Add(definition);
                definitions[canonical] = definition;
                if (kind == FieldKind.Raw) gaps.Add($"{reference}: opaque field kind on {canonical}.");
                if (Wire.Flag(field, "required")) rules.Add(new(canonical, RuleAction.Required, []));
                if (field.TryGetProperty("defaultValue", out var initial) && initial.ValueKind != JsonValueKind.Null)
                    gaps.Add($"{reference}: defaultValue on {canonical} cannot certify an effective supplied value.");
                if (Wire.Flag(global, "isPicklist"))
                {
                    var listId = Wire.GuidText(Wire.Text(global, "picklistId"));
                    var list = await Get(bound, $"_apis/work/processes/lists/{listId}?api-version={Api}", ct);
                    if (Wire.GuidText(Wire.Text(list, "id")) != listId) throw Wire.Invalid("Picklist identity differs.");
                    if (!Wire.Flag(global, "isPicklistSuggested") && !Wire.Flag(list, "isSuggested"))
                        rules.Add(new(canonical, RuleAction.Allowed, [], list.GetProperty("items").EnumerateArray()
                            .Select(v => Wire.RuleValue(v.GetString()!, kind)).Prepend(FieldValue.Null)));
                }
            }
            string CanonicalField(string value) => typeFields.FirstOrDefault(f =>
                f.Reference.Equals(value, StringComparison.OrdinalIgnoreCase))?.Reference
                ?? throw Wire.Invalid("A rule references an undiscovered type field.");
            foreach (var rule in Wire.Rows(await Get(bound, $"{route}/rules?api-version={Api}", ct)))
            {
                var disabled = Wire.Flag(rule, "isDisabled");
                if (disabled)
                {
                    // Retain native disabled semantics in the fingerprint without treating them as enabled permission/gaps.
                    rules.Add(new(CanonicalField("System.State"), RuleAction.Unknown, [],
                        [FieldValue.Raw(rule.GetRawText(), "ado-disabled-rule")], true));
                    continue;
                }
                if (rule.EnumerateObject().Any(p => p.Name is not ("id" or "name" or "url" or "isDisabled"
                    or "customizationType" or "conditions" or "actions")))
                    gaps.Add($"{reference}: unrecognized enabled rule metadata {rule.GetRawText()}");
                var conditions = new List<RuleCondition>();
                foreach (var condition in rule.GetProperty("conditions").EnumerateArray())
                {
                    var conditionType = Wire.Text(condition, "conditionType").TrimStart('$').ToLowerInvariant();
                    var field = CanonicalField(Wire.Text(condition, "field"));
                    var kind = conditionType switch
                    {
                        "when" => ConditionKind.Equal,
                        "whennot" => ConditionKind.NotEqual,
                        "whenwas" => ConditionKind.Was,
                        "whenchanged" => ConditionKind.Changed,
                        "whennotchanged" => ConditionKind.NotChanged,
                        _ => ConditionKind.Unknown,
                    };
                    var value = kind is ConditionKind.Equal or ConditionKind.NotEqual or ConditionKind.Was
                        ? Wire.RuleOperand(condition.GetProperty("value"), typeFields.First(f => f.Reference == field).Kind) : null;
                    conditions.Add(new(kind, field, value));
                    if (condition.EnumerateObject().Any(p => p.Name is not ("conditionType" or "field" or "value")))
                        gaps.Add($"{reference}: unrecognized condition metadata {condition.GetRawText()}");
                }
                var actions = rule.GetProperty("actions").EnumerateArray().ToArray();
                if (actions.Length == 0) gaps.Add($"{reference}: rule has no interpretable actions.");
                foreach (var action in actions)
                {
                    var field = CanonicalField(Wire.Text(action, "targetField"));
                    var actionType = Wire.Text(action, "actionType").TrimStart('$').ToLowerInvariant();
                    var kind = actionType switch
                    {
                        "makerequired" => RuleAction.Required,
                        "makereadonly" => RuleAction.ReadOnly,
                        "copyvalue" => RuleAction.Supplied,
                        _ => RuleAction.Unknown,
                    };
                    var values = kind == RuleAction.Supplied
                        ? new[] { Wire.RuleOperand(action.GetProperty("value"),
                            typeFields.First(f => f.Reference == field).Kind) } : [];
                    rules.Add(new(field, kind, conditions, values, disabled));
                    if (kind == RuleAction.Unknown || conditions.Any(c => c.Kind == ConditionKind.Unknown))
                        gaps.Add($"{reference}: enabled unsupported native rule {rule.GetRawText()}");
                    if (action.EnumerateObject().Any(p => p.Name is not ("actionType" or "targetField" or "value")))
                        gaps.Add($"{reference}: unrecognized action metadata {action.GetRawText()}");
                }
            }
            var states = Wire.Rows(await Get(bound, $"{route}/states?api-version={Api}", ct))
                .Where(s => !s.TryGetProperty("hidden", out var value) || !value.GetBoolean())
                .Select(s => new State(Wire.Text(s, "name"), Wire.Text(s, "stateCategory") switch
                {
                    "Proposed" => StateCategory.Proposed,
                    "InProgress" => StateCategory.InProgress,
                    "Resolved" => StateCategory.Resolved,
                    "Completed" => StateCategory.Completed,
                    "Removed" => StateCategory.Removed,
                    _ => StateCategory.Unknown,
                })).ToArray();
            var edges = new List<Transition>();
            var initialStates = new List<FieldValue>();
            if (!transitions.TryGetValue(reference, out var classic))
                gaps.Add($"{reference}: exact type transitions unavailable; no parent/all-pairs fallback.");
            else
                foreach (var edge in classic.GetProperty("transitions").EnumerateObject())
                    foreach (var destination in edge.Value.EnumerateArray())
                    {
                        var to = Wire.Text(destination, "to");
                        if (edge.Name.Length == 0) { initialStates.Add(FieldValue.String(to)); continue; }
                        edges.Add(new(edge.Name, to));
                    }
            // These are native protocol fields, not process-template vocabulary.
            var stateField = CanonicalField("System.State");
            if (initialStates.Count == 0) gaps.Add($"{reference}: initial transition states are unavailable.");
            else rules.Add(new(stateField, RuleAction.Allowed,
                [new(ConditionKind.Was, stateField, FieldValue.Null)], initialStates));
            types.Add(new(reference, stateField, typeFields, states, edges, rules,
                Wire.Flag(row, "isDisabled") || hidden.Contains(name)));
        }
        if (types.Count == 0) throw Wire.Invalid("The native process roster is empty.");
        var schema = new ProcessSchema(_connection, types, relations.Values.Where(r => r.Forward)
            .Select(r => new RelationKind(r.Reference, r.Shape, r.Directed)).Concat(resources), gaps);
        _capabilities = new(_capabilities.Operations.Where(o => o != OpKind.LinkResource)
            .Concat(resources.Count > 0 ? [OpKind.LinkResource] : Array.Empty<OpKind>()),
            schema.Relations.Select(r => r.Reference), [], true, true);
        return new(schema, names.ToImmutable(), relations.ToImmutableDictionary(StringComparer.Ordinal), definitions.ToImmutable(),
            nonItems.ToImmutable());
    }
}
