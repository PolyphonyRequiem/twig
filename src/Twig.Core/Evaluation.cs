using System.Collections.Immutable;

namespace Twig.Core;

public static partial class Core
{
    public static Result<bool> Matches(Query query, WorkItem item)
    {
        if (query.Kind == QueryKind.All) return Result<bool>.Ok(true);
        if (query.Kind == QueryKind.Equal)
            return item.Fields.TryGetValue(query.Field!, out var value)
                ? Result<bool>.Ok(value.Equals(query.Value))
                : Result<bool>.Fail(ErrorCode.Unavailable, $"Field '{query.Field}' was not observed.");
        if (query.Kind is QueryKind.Contains or QueryKind.Under or QueryKind.GreaterOrEqual)
        {
            if (!item.Fields.TryGetValue(query.Field!, out var value))
                return Result<bool>.Fail(ErrorCode.Unavailable, $"Field '{query.Field}' was not observed.");
            if (value.Kind == FieldKind.Null) return Result<bool>.Ok(false);
            if (value.Kind != query.Value!.Kind)
                return Result<bool>.Fail(ErrorCode.Unavailable, "The observed query field does not have the declared operand type.");
            return Result<bool>.Ok(query.Kind switch
            {
                QueryKind.Contains => value.Text!.Contains(query.Value.Text!, StringComparison.OrdinalIgnoreCase),
                QueryKind.Under => value.Text!.Equals(query.Value.Text!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || value.Text.StartsWith(query.Value.Text.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase),
                QueryKind.GreaterOrEqual => value.Kind == FieldKind.Instant ? value.Instant >= query.Value.Instant : value.Number >= query.Value.Number,
                _ => false,
            });
        }
        var children = query.Children.Select(c => Matches(c, item)).ToArray();
        if (query.Kind == QueryKind.And && children.Any(c => c.IsSuccess && !c.Value))
            return Result<bool>.Ok(false);
        if (query.Kind == QueryKind.Or && children.Any(c => c.IsSuccess && c.Value))
            return Result<bool>.Ok(true);
        var unavailable = children.FirstOrDefault(c => !c.IsSuccess);
        if (unavailable is not null) return unavailable;
        return Result<bool>.Ok(query.Kind switch
        {
            QueryKind.And => true,
            QueryKind.Or => false,
            QueryKind.Not => !children[0].Value,
            _ => throw new InvalidOperationException("The query constructor rejects unknown operators."),
        });
    }

    public static Result<ImmutableArray<ItemKey>> ResolveBench(Bench bench, StoreSnapshot store, ProcessSchema schema)
    {
        var selected = new HashSet<ItemKey>();
        foreach (var selector in bench.Selectors)
        {
            if (selector.Kind == SelectorKind.Query)
            {
                foreach (var item in store.Items)
                {
                    var match = Matches(selector.Query!, item);
                    if (!match.IsSuccess) return Result<ImmutableArray<ItemKey>>.Fail(match.Error);
                    if (match.Value) selected.Add(item.Key);
                }
                continue;
            }
            if (!store.Items.Any(i => i.Key == selector.Item) && !store.Drafts.Any(d => d.Key == selector.Item))
                return Result<ImmutableArray<ItemKey>>.Fail(ErrorCode.Unavailable, "A pinned item is not available locally.");
            selected.Add(selector.Item!);
            if (selector.Kind == SelectorKind.Pin) continue;
            if (!schema.Relations.Any(r => r.Reference == selector.HierarchyRelation
                && r.Shape == RelationShape.Hierarchy && r.Directed))
                return Result<ImmutableArray<ItemKey>>.Fail(ErrorCode.Unsupported, "Subtree selector needs a discovered directed hierarchy.");
            var visited = new HashSet<ItemKey>();
            var queue = new Queue<ItemKey>();
            queue.Enqueue(selector.Item!);
            while (queue.TryDequeue(out var key))
            {
                if (!visited.Add(key))
                    return Result<ImmutableArray<ItemKey>>.Fail(ErrorCode.Conflict, "Hierarchy cycle or repeated descendant prevents a trustworthy subtree.");
                var item = store.Items.FirstOrDefault(i => i.Key == key);
                if (item?.RelationshipsComplete != true)
                    return Result<ImmutableArray<ItemKey>>.Fail(ErrorCode.Unavailable, "Subtree relationship evidence is incomplete.");
                selected.Add(key);
                foreach (var edge in LogicalRelationships(store, schema).Where(r => r.From == key && r.Kind == selector.HierarchyRelation))
                    queue.Enqueue(edge.To);
            }
        }
        return Result<ImmutableArray<ItemKey>>.Ok(selected.OrderBy(KeyText, StringComparer.Ordinal).ToImmutableArray());
    }

    public static ImmutableArray<Finding> Evaluate(Policy policy, StoreSnapshot before, StoreSnapshot after, ProcessSchema schema)
    {
        var findings = ImmutableArray.CreateBuilder<Finding>();
        if (policy.Rules.IsEmpty)
            return [new("policy", FindingKind.EvidenceUnavailable, null,
                "An empty declaration cannot certify policy compliance.", true, [])];
        if (after.Relationships.Length > 4000 || before.Relationships.Length > 4000)
            return [new("policy", FindingKind.EvidenceUnavailable, null,
                "Relationship observations exceed the bounded policy evidence budget.", true, [])];
        var edges = LogicalRelationships(after, schema).ToArray();
        var oldEdges = LogicalRelationships(before, schema).ToArray();
        foreach (var rule in policy.Rules)
        {
            void Unavailable(ItemKey? item, string reason) =>
                findings.Add(new(rule.Id, FindingKind.EvidenceUnavailable, item, reason, true, []));
            void Violation(ItemKey key, string reason, ImmutableArray<Relationship> evidence, bool historical) =>
                findings.Add(new(rule.Id, FindingKind.Violation, key, reason, !rule.WarnOnly && !historical, evidence));
            if (!schema.Gaps.IsEmpty)
            {
                Unavailable(null, "Schema discovery is incomplete; policy cannot certify compliance.");
                continue;
            }
            var shape = rule.Kind == RuleKind.ParentType ? RelationShape.Hierarchy : RelationShape.Dependency;
            var kinds = schema.Relations.Where(r => r.Shape == shape && r.Directed)
                .Select(r => r.Reference).ToHashSet(StringComparer.Ordinal);
            if (kinds.Count == 0)
            {
                Unavailable(null, "The relation semantics needed by this rule are unavailable.");
                continue;
            }
            var relevant = edges.Where(e => kinds.Contains(e.Kind)).ToArray();
            if (after.Items.Length > rule.MaxNodes)
            {
                Unavailable(null, "Policy traversal exceeded the declared evidence bound.");
                continue;
            }
            if (after.Items.IsEmpty || after.Items.Any(i => !i.RelationshipsComplete))
                Unavailable(null, "Complete scoped relationship observations are unavailable.");
            if (after.Relationships.Any(e => !e.Deleted && !schema.Relations.Any(r => r.Reference == e.Kind))
                || after.Items.Any(i => !schema.Types.Any(t => t.Reference == i.Type)))
                Unavailable(null, "Observed type or relationship semantics are outside the discovered schema.");
            foreach (var edge in relevant)
            {
                var source = after.Items.FirstOrDefault(i => i.Key == edge.From);
                var target = after.Items.FirstOrDefault(i => i.Key == edge.To);
                if (source is null || target is null)
                {
                    Unavailable(source is null ? edge.From : edge.To, "A relationship endpoint is outside the acquired evidence.");
                    continue;
                }
                var previousSource = before.Items.FirstOrDefault(i => i.Key == edge.From);
                var previousTarget = before.Items.FirstOrDefault(i => i.Key == edge.To);
                var unchanged = oldEdges.Any(e => SameEdge(e, edge))
                    && previousSource?.Type == source.Type && previousTarget?.Type == target.Type
                    && previousSource.RelationshipsComplete && previousTarget.RelationshipsComplete;
                if (rule.Kind == RuleKind.ParentType)
                {
                    if (target.Type == rule.ChildType && !rule.AllowedParentTypes.Contains(source.Type, StringComparer.Ordinal))
                        Violation(target.Key, "Declared child type has a disallowed parent type.", [edge], unchanged);
                    continue;
                }
                if (edge.From == edge.To)
                {
                    Violation(edge.From, "Dependency is a self-link.", [edge], unchanged);
                    continue;
                }
                var duplicates = after.Relationships.Where(e => !e.Deleted && e.Kind == edge.Kind
                    && e.From == edge.From && e.To == edge.To && e.Origin == edge.Origin).ToArray();
                if (duplicates.Length > 1)
                {
                    var priorCount = before.Relationships.Count(e => !e.Deleted && SameEdge(e, edge) && e.Origin == edge.Origin);
                    Violation(edge.From, "Duplicate dependency observations were supplied.",
                        duplicates.ToImmutableArray(), unchanged && priorCount >= duplicates.Length);
                }
                var path = FindPath(edge.To, edge.From, relevant, rule.MaxNodes);
                if (!path.IsSuccess) Unavailable(edge.From, path.Error.Message);
                else if (!path.Value.IsEmpty)
                {
                    var witness = path.Value.Add(edge);
                    var historical = unchanged && witness.All(e => oldEdges.Any(o => SameEdge(o, e)));
                    Violation(edge.From, "Dependency closes a witnessed cycle.", witness, historical);
                }
            }
        }
        return findings.ToImmutable();
    }

    private static Result<ImmutableArray<Relationship>> FindPath(ItemKey from, ItemKey to, Relationship[] edges, int maxNodes)
    {
        var queue = new Queue<(ItemKey Key, ImmutableArray<Relationship> Path)>();
        var visited = new HashSet<ItemKey>();
        queue.Enqueue((from, []));
        while (queue.TryDequeue(out var entry))
        {
            if (!visited.Add(entry.Key)) continue;
            if (visited.Count > maxNodes)
                return Result<ImmutableArray<Relationship>>.Fail(ErrorCode.Unavailable, "Dependency traversal reached its evidence limit.");
            foreach (var edge in edges.Where(e => e.From == entry.Key))
            {
                var path = entry.Path.Add(edge);
                if (edge.To == to) return Result<ImmutableArray<Relationship>>.Ok(path);
                queue.Enqueue((edge.To, path));
            }
        }
        return Result<ImmutableArray<Relationship>>.Ok([]);
    }

    private static IEnumerable<Relationship> LogicalRelationships(StoreSnapshot store, ProcessSchema schema)
    {
        // Provenance is retained in storage; matching native/local observations form one logical edge.
        return store.Relationships.Where(r => !r.Deleted)
            .Select(r => schema.Relations.Any(k => k.Reference == r.Kind && !k.Directed)
                && string.CompareOrdinal(KeyText(r.From), KeyText(r.To)) > 0
                    ? r with { From = r.To, To = r.From } : r)
            .DistinctBy(r => (r.From, r.To, r.Kind));
    }

    private static bool SameEdge(Relationship first, Relationship second)
        => first.From == second.From && first.To == second.To && first.Kind == second.Kind;
}
