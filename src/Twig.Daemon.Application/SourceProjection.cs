using System.Collections.Immutable;
using Bench.Domain;
using Twig.Core;
using Model = Twig.Core.Core;

namespace Bench.Application;

public static class SourceProjection
{
    public static Result<SourceView> CreateOwnedSeeds(BenchDefinition definition, SourceSelection selection,
        SourceObservation observation, ImmutableArray<Draft> editable, ImmutableArray<ItemKey> receipts, CancellationToken ct)
    {
        if (editable.Any(draft => !draft.Key.IsLocal || draft.Baseline is not null
            || !observation.Snapshot.Drafts.Contains(draft) || receipts.Contains(draft.Key)
            || observation.Snapshot.Mappings.Any(mapping => mapping.Local == draft.Key)))
            return Result<SourceView>.Fail(ErrorCode.Conflict, "Owned seed and cached draft observations changed; reload the same native source.");
        var store = observation.Snapshot;
        var projected = observation with { Snapshot = new(store.Version, store.Items, store.Relationships,
            store.Drafts.Where(draft => !receipts.Contains(draft.Key)), store.Proposals, store.Journal, store.Mappings, store.Benches) };
        var temporary = new SourceSelection(selection.SourceId, selection.Binding, selection.Selectors.AddRange(editable.Select(draft =>
            new Selector(SelectorKind.Pin, draft.Key, connection: selection.Binding.Connection))));
        return Create(definition, temporary, projected, ct);
    }

    public static Result<SourceView> Create(BenchDefinition definition, SourceSelection selection, SourceObservation observation,
        CancellationToken ct = default)
    {
        var store = observation.Snapshot;
        var schema = observation.Schema;
        if (selection.Binding != observation.Binding || schema.Connection != selection.Binding.Connection)
            return Result<SourceView>.Fail(ErrorCode.Conflict, "Source observation does not match the frozen binding.");
        if (store.Items.Length + store.Drafts.Length > 10000 || store.Relationships.Length > 20000)
            return Result<SourceView>.Fail(ErrorCode.Partial, "The source exceeds the viewer's bounded evidence budget.");
        long work = 0, characters = 0;
        foreach (var selector in selection.Selectors)
        {
            long nodes = 1;
            if (selector.Query is { } query)
            {
                var pending = new Stack<Query>(); pending.Push(query); nodes = 0;
                while (pending.TryPop(out var part)) { nodes++; foreach (var child in part.Children) pending.Push(child); }
            }
            work += selector.Kind == SelectorKind.Subtree
                ? (long)store.Items.Length * (store.Items.Length + store.Relationships.Length)
                : nodes * (store.Items.Length + store.Drafts.Length);
            if (work > 10_000_000) return Result<SourceView>.Fail(ErrorCode.Partial, "Membership evaluation exceeds the bounded comparison budget.");
        }
        foreach (var fields in store.Items.Select(i => i.Fields).Concat(store.Drafts.Select(d => d.Changes))
            .Concat(store.Drafts.Where(d => d.Baseline is not null).Select(d => d.Baseline!.Fields)))
        foreach (var field in fields)
        {
            characters += field.Key.Length + (field.Value.Text?.Length ?? 0) + 32;
            foreach (var value in field.Value.Values) characters += value.Length;
            if (characters > 4 * 1024 * 1024) return Result<SourceView>.Fail(ErrorCode.Partial, "Cached field evidence exceeds the projection byte budget.");
        }
        ct.ThrowIfCancellationRequested();
        if (store.Mappings.GroupBy(m => m.Local).Any(g => g.Select(m => m.Remote).Distinct().Count() != 1)
            || store.Mappings.Any(m => !m.Remote.BelongsTo(selection.Binding.Connection))
            || store.Items.Any(i => !i.Key.IsLocal && !i.Key.BelongsTo(selection.Binding.Connection)))
            return Result<SourceView>.Fail(ErrorCode.Conflict, "Cached source identity or permanent mapping is ambiguous.");
        ItemKey Resolve(ItemKey key) => store.Mappings.FirstOrDefault(m => m.Local == key)?.Remote ?? key;
        var normalizedSelectors = selection.Selectors.Select(s => s.Item is null ? s
            : new Selector(s.Kind, Resolve(s.Item), hierarchyRelation: s.HierarchyRelation));
        var normalizedEdges = store.Relationships.Select(e => e with { From = Resolve(e.From), To = Resolve(e.To) }).ToArray();
        var normalized = new StoreSnapshot(store.Version, store.Items, normalizedEdges, store.Drafts,
            store.Proposals, store.Journal, store.Mappings, store.Benches);
        var members = Model.ResolveBench(new(definition.Id, definition.Name, normalizedSelectors), normalized, schema, observation.CacheReadAt);
        ct.ThrowIfCancellationRequested();
        if (!members.IsSuccess) return Result<SourceView>.Fail(members.Error);
        var selected = members.Value.Select(Resolve).ToHashSet();
        var candidates = selected.Concat(store.Drafts.Select(draft => Resolve(draft.Key))).Distinct().ToArray();
        var exclusions = observation.WorkspaceExclusions.Select(rule => rule.Item is null ? rule
            : new Selector(rule.Kind, Resolve(rule.Item), hierarchyRelation: rule.HierarchyRelation, connection: rule.Connection));
        var visible = Model.ApplyViewExclusions(candidates, exclusions, normalized, schema, observation.CacheReadAt);
        if (!visible.IsSuccess) return Result<SourceView>.Fail(visible.Error);
        var displayed = visible.Value.ToHashSet();
        var rows = ImmutableArray.CreateBuilder<ItemView>();
        foreach (var key in displayed.Concat(store.Drafts.Select(d => Resolve(d.Key))).Distinct())
        {
            var item = store.Items.SingleOrDefault(i => i.Key == key);
            var drafts = store.Drafts.Where(d => Resolve(d.Key) == key).ToArray();
            if (drafts.Length > 1)
                return Result<SourceView>.Fail(ErrorCode.Conflict, "Several retained drafts resolve to one native item.");
            var draft = drafts.SingleOrDefault();
            var type = draft?.Type ?? item?.Type;
            if (type is null)
                return Result<SourceView>.Fail(ErrorCode.Unavailable, "A selected member has no local item or draft evidence.");
            rows.Add(new(new(selection.SourceId, key), type, selected.Contains(key), draft is not null,
                item?.RelationshipsComplete == true, item?.Fields ?? draft?.Baseline?.Fields ?? FieldValues.Empty,
                draft?.Changes ?? FieldValues.Empty, schema.Types.SingleOrDefault(t => t.Reference == type)?.StateField)
                { IsExcluded = !displayed.Contains(key) });
        }
        var notes = schema.Gaps.Concat(schema.Types.SelectMany(type => type.Gaps)).ToList();
        if (rows.Any(i => !i.RelationshipsComplete))
            notes.Add("Relationship observations are incomplete. Missing links are not authoritative leaf evidence.");
        if (rows.Any(i => !schema.Types.Any(t => t.Reference == i.Type)))
            notes.Add("Some retained item types are outside the discovered schema; no type metadata was inferred.");
        if (observation.Mode == ObservationMode.PermittedOfflineCache)
            notes.Add("Offline cached evidence under an unchanged admitted binding; upstream revocation may not yet be known.");
        if (observation.Mode == ObservationMode.OnlineValidatedCache)
            notes.Add("Current connection read access was validated online; schema and presentation remain from the explicit metadata check.");
        notes.Add("Cache-read time is not a provider item freshness timestamp.");
        var stamp = new ObservationStamp(observation.Binding, store.Version, observation.CacheReadAt,
            schema.Fingerprint, observation.SchemaObservedAt, observation.Mode);
        return Result<SourceView>.Ok(new(selection.SourceId, stamp, rows.ToImmutable(),
            Enumerables(selection.SourceId, schema), Hierarchy(selection.SourceId, normalized, schema, selected.Intersect(displayed).ToHashSet(), Resolve),
            notes.ToImmutableArray(), null, schema.Types.Select(t => new TypeView(t.Reference, t.Presentation,
                t.Fields, t.States) { Gaps = t.Gaps }).ToImmutableArray(), schema.WebUrl));
    }

    public static ImmutableArray<EnumerableField> Enumerables(Guid source, ProcessSchema schema)
    {
        var choices = new SortedDictionary<string, List<FieldChoice>>(StringComparer.Ordinal);
        foreach (var type in schema.Types)
        foreach (var field in type.Fields)
        {
            var values = new List<FieldChoice>();
            if (field.Reference == type.StateField)
                values.AddRange(type.States.Select(s => new FieldChoice(type.Reference, FieldValue.String(s.Name), false)));
            if (field.Kind == FieldKind.Boolean)
                values.AddRange([new(type.Reference, FieldValue.Bool(false), false), new(type.Reference, FieldValue.Bool(true), false)]);
            foreach (var rule in type.Rules.Where(r => !r.Disabled && r.Action == RuleAction.Allowed && r.Field == field.Reference))
                values.AddRange(rule.Values.Where(v => v.Kind == field.Kind || v.Kind == FieldKind.Null)
                    .Select(v => new FieldChoice(type.Reference, v, !rule.Conditions.IsEmpty)));
            if (values.Count == 0) continue;
            if (!choices.TryGetValue(field.Reference, out var catalog)) choices.Add(field.Reference, catalog = []);
            catalog.AddRange(values);
        }
        return choices.Select(pair => new EnumerableField(source, pair.Key, pair.Value.Distinct().ToImmutableArray(),
            pair.Value.Any(v => v.Value.Kind == FieldKind.StringList))).ToImmutableArray();
    }

    public static ImmutableArray<ValueGroup> Group(SourceView view, string reference)
    {
        var field = view.Fields.SingleOrDefault(f => f.Reference == reference)
            ?? throw new ArgumentException("Grouping requires a discovered source-qualified enumerable field.");
        var groups = new List<(FieldValue? Value, bool Observed, bool InCatalog, List<QualifiedItem> Members)>();
        foreach (var item in view.Items.Where(i => i.BenchMember && !i.IsExcluded))
        {
            var observed = item.EffectiveFields.TryGetValue(reference, out var value);
            var listed = observed && field.Choices.Any(c => c.Type == item.Type && c.Value.Equals(value));
            var index = groups.FindIndex(g => g.Observed == observed && g.InCatalog == listed
                && (observed ? g.Value!.Equals(value) : g.Value is null));
            if (index < 0) groups.Add((observed ? value : null, observed, listed, [item.Identity]));
            else groups[index].Members.Add(item.Identity);
        }
        return groups.Select(g => new ValueGroup(g.Value, g.Observed, g.InCatalog,
            g.Value?.Kind == FieldKind.StringList, g.Members.ToImmutableArray())).ToImmutableArray();
    }

    private static HierarchyView Hierarchy(Guid source, StoreSnapshot store, ProcessSchema schema,
        HashSet<ItemKey> members, Func<ItemKey, ItemKey> resolve)
    {
        QualifiedItem Qualified(ItemKey key) => new(source, resolve(key));
        var visible = members.Concat(store.Drafts.Select(d => resolve(d.Key))).ToHashSet();
        var evidence = store.Relationships.Where(e => visible.Contains(resolve(e.From)) || visible.Contains(resolve(e.To)))
            .Select(e => new HierarchyEdge(Qualified(e.From), Qualified(e.To),
            e.Kind, e.Origin, e.LocalVersion, e.Deleted, false)).ToList();
        foreach (var draft in store.Drafts.Where(d => d.Parent is not null
            && !store.Mappings.Any(m => m.Local == d.Key)))
            evidence.Add(new(Qualified(draft.Parent!), Qualified(draft.Key), draft.ParentRelation!,
                RelationshipOrigin.Local, store.Version, false, true));
        var kinds = schema.Relations.Where(r => r.Directed && r.Shape == RelationShape.Hierarchy).ToArray();
        if (kinds.Length != 1)
            return new(false, null, evidence.ToImmutableArray(),
                ["Tree requires exactly one discovered directed hierarchy; no source or relation was guessed."]);
        var kind = kinds[0].Reference;
        var edges = evidence.Where(e => !e.Deleted && e.Relation == kind)
            .Select(e => (From: e.From.Key, To: e.To.Key)).Distinct().ToArray();
        var inside = edges.Where(e => members.Contains(e.From) && members.Contains(e.To)).ToArray();
        if (inside.GroupBy(e => e.To).Any(g => g.Select(e => e.From).Distinct().Count() > 1))
            return new(false, kind, evidence.ToImmutableArray(), ["Multiple cached parents prevent a single hierarchy forest."]);
        var degrees = members.ToDictionary(k => k, _ => 0);
        var children = inside.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).ToArray());
        foreach (var edge in inside) degrees[edge.To]++;
        var roots = new Queue<ItemKey>(degrees.Where(p => p.Value == 0).Select(p => p.Key));
        var seen = 0;
        while (roots.TryDequeue(out var key))
        {
            seen++;
            if (!children.TryGetValue(key, out var descendants)) continue;
            foreach (var child in descendants)
                if (--degrees[child] == 0) roots.Enqueue(child);
        }
        if (seen != members.Count)
            return new(false, kind, evidence.ToImmutableArray(), ["Cached hierarchy cycle; list membership remains available."]);
        var limitations = new List<string>();
        if (edges.Any(e => members.Contains(e.From) != members.Contains(e.To)))
            limitations.Add("Outside-view hierarchy endpoints are context, not visible members.");
        return new(true, kind, evidence.ToImmutableArray(), limitations.ToImmutableArray());
    }
}
