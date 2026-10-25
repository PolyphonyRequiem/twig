using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Policy;

/// <summary>
/// Evaluates a <see cref="DeclaredPolicy"/> against a proposal's post-change state (AB#1169).
/// </summary>
/// <remarks>
/// <para>
/// Every rule yields either one <see cref="PolicyOutcome.Permit"/> summary or one decision per
/// finding. A finding the cache cannot settle is <see cref="PolicyOutcome.Unavailable"/> — the
/// engine never permits what it could not check (D3). A violation already present before the
/// proposal and not worsened by it is reported as <see cref="PolicyOutcome.Warn"/> (D4).
/// </para>
/// <para>
/// Known limits of this slice: a type change on a <em>parent</em> does not re-judge its
/// existing children, and staged seed links are not part of the dependency graph.
/// </para>
/// </remarks>
public sealed class DeclaredPolicyEvaluator(IWorkItemRepository items, IWorkItemLinkRepository? links)
{
    internal const string TypeField = "System.WorkItemType";
    private const string ParentRelation = "parent";
    private const string PredecessorRelation = "predecessor";
    private const string SuccessorRelation = "successor";

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    /// <summary>Evaluates every rule in declared order.</summary>
    public async Task<IReadOnlyList<PolicyDecision>> EvaluateAsync(
        DeclaredPolicy policy, PlanDefinition plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(plan);
        if (policy.Rules.Count == 0)
            return [];

        var context = await ProposalState.LoadAsync(items, plan, ct).ConfigureAwait(false);
        var decisions = new List<PolicyDecision>();
        foreach (var rule in policy.Rules)
        {
            var found = rule switch
            {
                ParentTypeRule r => EvaluateParentType(r, context),
                LabelApplicabilityRule r => EvaluateLabels(r, context),
                DependencyIntegrityRule r => await EvaluateDependenciesAsync(r, plan, ct).ConfigureAwait(false),
                _ => [Decide(rule, PolicyOutcome.Unavailable, $"Rule kind '{rule.Kind}' is not supported by this Twig build.")],
            };
            decisions.AddRange(found);
        }

        return decisions;
    }

    private static List<PolicyDecision> EvaluateParentType(ParentTypeRule rule, ProposalState state)
    {
        var findings = new List<PolicyDecision>();
        var judged = 0;
        foreach (var edge in state.ParentEdges())
        {
            var childType = state.PostType(edge.ChildId);
            if (childType is null)
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the type of #{edge.ChildId}; refresh it with 'twig show {edge.ChildId} --refresh'.",
                    edge.ChildId, edge.OpId));
                continue;
            }

            if (!Names.Equals(childType, rule.ChildType))
                continue;

            judged++;
            var parentType = state.PostType(edge.ParentId);
            if (parentType is null)
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the type of parent #{edge.ParentId} of {rule.ChildType} #{edge.ChildId}; refresh it with 'twig show {edge.ParentId} --refresh'.",
                    edge.ChildId, edge.OpId));
            }
            else if (!rule.AllowedParentTypes.Contains(parentType, Names))
            {
                var existing = state.PreParent(edge.ChildId) == edge.ParentId
                    && Names.Equals(state.PreType(edge.ChildId), childType)
                    && Names.Equals(state.PreType(edge.ParentId), parentType);
                findings.Add(Decide(rule, existing ? PolicyOutcome.Warn : Violation(rule),
                    (existing ? "Pre-existing, not worsened by this proposal: " : "")
                    + $"{rule.ChildType} #{edge.ChildId} may not be parented under {parentType} #{edge.ParentId}; allowed parent types: {string.Join(", ", rule.AllowedParentTypes)}.",
                    edge.ChildId, edge.OpId));
            }
        }

        return findings.Count > 0
            ? findings
            : [Decide(rule, PolicyOutcome.Permit, $"{judged} new {rule.ChildType} parent edge(s) checked; all parents are of an allowed type.")];
    }

    private static List<PolicyDecision> EvaluateLabels(LabelApplicabilityRule rule, ProposalState state)
    {
        var findings = new List<PolicyDecision>();
        var judged = 0;
        foreach (var target in state.LabelTargets(rule.Field))
        {
            judged++;
            var postTags = state.PostTags(target.Id, rule.Field);
            var postType = state.PostType(target.Id);
            if (!postTags.Any(t => HasPrefix(t, rule.Prefix)))
                continue;

            if (postType is null || (!target.IsNewSeed && !state.IsKnown(target.Id)))
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cannot check '{rule.Prefix}' labels on #{target.Id}: its current state is not cached; refresh it with 'twig show {target.Id} --refresh'.",
                    target.Id, target.OpId));
                continue;
            }

            var after = LabelViolations(rule, postType, postTags);
            if (after.Count == 0)
                continue;

            var before = target.IsNewSeed
                ? new Dictionary<string, string>()
                : LabelViolations(rule, state.PreType(target.Id)!, state.PreTags(target.Id, rule.Field));
            var added = after.Where(v => !before.ContainsKey(v.Key)).ToList();
            if (added.Count == 0)
            {
                findings.Add(Decide(rule, PolicyOutcome.Warn,
                    $"Pre-existing, not worsened by this proposal: {string.Join("; ", after.Values)}.",
                    target.Id, target.OpId));
                continue;
            }

            foreach (var violation in added)
                findings.Add(Decide(rule, Violation(rule), violation.Value, target.Id, target.OpId));
        }

        return findings.Count > 0
            ? findings
            : [Decide(rule, PolicyOutcome.Permit, $"{judged} item(s) with changed labels or type checked for '{rule.Prefix}' labels.")];
    }

    private static Dictionary<string, string> LabelViolations(
        LabelApplicabilityRule rule, string type, IReadOnlyList<string> tags)
    {
        var violations = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixed = tags.Where(t => HasPrefix(t, rule.Prefix)).ToList();
        if (!rule.AllowedTypes.Contains(type, Names))
        {
            foreach (var tag in prefixed)
                violations[$"type:{tag.ToLowerInvariant()}"] =
                    $"label '{tag}' is not allowed on {type}; '{rule.Prefix}' labels apply to: {string.Join(", ", rule.AllowedTypes)}";
        }

        if (rule.Exclusive && prefixed.Count > 1)
        {
            var key = string.Join("|", prefixed.Select(t => t.ToLowerInvariant()).Order(StringComparer.Ordinal));
            violations[$"exclusive:{key}"] =
                $"at most one '{rule.Prefix}' label is allowed, found: {string.Join(", ", prefixed)}";
        }

        return violations;
    }

    private async Task<List<PolicyDecision>> EvaluateDependenciesAsync(
        DependencyIntegrityRule rule, PlanDefinition plan, CancellationToken ct)
    {
        var findings = new List<PolicyDecision>();
        var adds = new List<(int From, int To, string OpId, int Source)>();
        var removes = new HashSet<(int, int)>();
        foreach (var op in plan.Operations)
        {
            switch (op)
            {
                case AddLinkOperation add when add.WorkItemId == add.OtherId:
                    findings.Add(Decide(rule, Violation(rule),
                        $"#{add.WorkItemId} cannot be linked to itself ({add.Relation}).", add.WorkItemId, add.Id));
                    break;
                case AddLinkOperation add when Normalize(add.Relation, add.WorkItemId, add.OtherId) is { } edge:
                    adds.Add((edge.From, edge.To, add.Id, add.WorkItemId));
                    break;
                case RemoveLinkOperation remove when Normalize(remove.Relation, remove.WorkItemId, remove.OtherId) is { } edge:
                    removes.Add(edge);
                    break;
            }
        }

        if (adds.Count == 0)
            return findings.Count > 0 ? findings : [Decide(rule, PolicyOutcome.Permit, "No new dependency edges.")];

        if (links is null)
        {
            foreach (var add in adds)
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Dependency links are not available to check #{add.From} → #{add.To}.", add.Source, add.OpId));
            return findings;
        }

        var graph = new DependencyGraph(links, adds.Select(a => (a.From, a.To)), removes);
        var seen = new HashSet<(int, int)>();
        foreach (var add in adds)
        {
            var edge = (add.From, add.To);
            if (!seen.Add(edge))
            {
                findings.Add(Decide(rule, Violation(rule),
                    $"Dependency #{add.From} → #{add.To} is declared more than once in this proposal.", add.Source, add.OpId));
                continue;
            }

            var cached = await graph.CachedEdgeAsync(add.From, add.To, ct).ConfigureAwait(false);
            if (cached is null)
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Links of #{add.From} have not been verified; refresh with 'twig show {add.From} --refresh' to check #{add.From} → #{add.To}.",
                    add.Source, add.OpId));
                continue;
            }

            if (cached.Value)
            {
                findings.Add(Decide(rule, Violation(rule),
                    $"Dependency #{add.From} → #{add.To} already exists.", add.Source, add.OpId));
                continue;
            }

            var search = await graph.ReachesAsync(add.To, add.From, rule.MaxNodes, ct).ConfigureAwait(false);
            if (search.Found)
            {
                findings.Add(Decide(rule, Violation(rule),
                    $"Dependency #{add.From} → #{add.To} would create a cycle: #{add.To} already leads back to #{add.From}.",
                    add.Source, add.OpId));
            }
            else if (search.Truncated)
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cycle check for #{add.From} → #{add.To} exceeded {rule.MaxNodes} items; it could not be completed.",
                    add.Source, add.OpId));
            }
            else if (search.Unverified.Count > 0)
            {
                var ids = string.Join(", ", search.Unverified.Order().Take(10).Select(i => $"#{i}"));
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cycle check for #{add.From} → #{add.To} reached items whose links are not verified ({ids}); run 'twig refresh' or 'twig show <id> --refresh'.",
                    add.Source, add.OpId));
            }
        }

        return findings.Count > 0
            ? findings
            : [Decide(rule, PolicyOutcome.Permit, $"{adds.Count} new dependency edge(s) checked: no self-links, duplicates or cycles.")];
    }

    /// <summary>Normalizes a dependency relation to a from→to edge (from must finish first).</summary>
    private static (int From, int To)? Normalize(string relation, int workItemId, int otherId)
    {
        if (Names.Equals(relation, PredecessorRelation))
            return (otherId, workItemId);
        if (Names.Equals(relation, SuccessorRelation))
            return (workItemId, otherId);
        return null;
    }

    private static bool HasPrefix(string tag, string prefix) =>
        tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static PolicyOutcome Violation(DeclaredPolicyRule rule) =>
        rule.Enforcement == PolicyEnforcement.Warn ? PolicyOutcome.Warn : PolicyOutcome.Refuse;

    private static PolicyDecision Decide(
        DeclaredPolicyRule rule, PolicyOutcome outcome, string reason, int? workItemId = null, string? opId = null) =>
        new()
        {
            RuleId = rule.Id,
            Kind = rule.Kind,
            Outcome = outcome,
            Reason = reason,
            WorkItemId = workItemId,
            OpId = opId,
        };

    internal static IReadOnlyList<string> SplitTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly record struct ParentEdge(int ChildId, int ParentId, string OpId);

    private readonly record struct LabelTarget(int Id, string OpId, bool IsNewSeed);

    /// <summary>Cached pre-change items plus the proposal's overlays.</summary>
    private sealed class ProposalState
    {
        private readonly PlanDefinition _plan;
        private readonly Dictionary<int, WorkItem> _cached;
        private readonly Dictionary<int, WorkItem> _seeds;
        private readonly Dictionary<int, string> _publishedSeedOps;

        private ProposalState(
            PlanDefinition plan, Dictionary<int, WorkItem> cached, Dictionary<int, WorkItem> seeds,
            Dictionary<int, string> publishedSeedOps)
        {
            _plan = plan;
            _cached = cached;
            _seeds = seeds;
            _publishedSeedOps = publishedSeedOps;
        }

        public static async Task<ProposalState> LoadAsync(IWorkItemRepository items, PlanDefinition plan, CancellationToken ct)
        {
            var seeds = new Dictionary<int, WorkItem>();
            var published = new Dictionary<int, string>();
            if (plan.Operations.Any(o => o is PublishSeedOperation))
            {
                var all = await items.GetSeedsAsync(ct).ConfigureAwait(false);
                foreach (var seed in all.Where(s => s.Id < 0))
                    seeds[seed.Id] = seed;
                var byIdentity = all.Where(s => s.Id < 0 && s.StagedIdentity is not null)
                    .ToDictionary(s => s.StagedIdentity!.Value.Value);
                foreach (var op in plan.Operations.OfType<PublishSeedOperation>())
                {
                    if (byIdentity.TryGetValue(op.StagedIdentity.Value, out var seed))
                        published[seed.Id] = op.Id;
                }
            }

            var ids = new HashSet<int>();
            foreach (var op in plan.Operations)
            {
                switch (op)
                {
                    case BatchOperation b: ids.Add(b.WorkItemId); break;
                    case AddLinkOperation a: ids.Add(a.WorkItemId); ids.Add(a.OtherId); break;
                    case RemoveLinkOperation r: ids.Add(r.WorkItemId); ids.Add(r.OtherId); break;
                    case DeleteOperation d: ids.Add(d.WorkItemId); break;
                }
            }

            foreach (var seedId in published.Keys)
            {
                if (seeds[seedId].ParentId is { } parent)
                    ids.Add(parent);
            }

            ids.RemoveWhere(i => i <= 0);
            var cached = ids.Count == 0
                ? new Dictionary<int, WorkItem>()
                : (await items.GetByIdsAsync(ids, ct).ConfigureAwait(false)).ToDictionary(w => w.Id);

            // A type change re-judges the item's existing parent, which no operation names.
            var parentsOfRetyped = plan.Operations.OfType<BatchOperation>()
                .Where(b => b.Fields.Keys.Any(k => Names.Equals(k, TypeField)))
                .Select(b => cached.GetValueOrDefault(b.WorkItemId)?.ParentId)
                .OfType<int>()
                .Where(p => p > 0 && !cached.ContainsKey(p))
                .ToHashSet();
            if (parentsOfRetyped.Count > 0)
            {
                foreach (var parent in await items.GetByIdsAsync(parentsOfRetyped, ct).ConfigureAwait(false))
                    cached[parent.Id] = parent;
            }

            return new ProposalState(plan, cached, seeds, published);
        }

        public bool IsKnown(int id) => id < 0 ? _seeds.ContainsKey(id) : _cached.ContainsKey(id);

        private WorkItem? Find(int id) =>
            id < 0 ? _seeds.GetValueOrDefault(id) : _cached.GetValueOrDefault(id);

        public string? PreType(int id) => Find(id)?.Type.Value is { Length: > 0 } t ? t : null;

        public int? PreParent(int id) => Find(id)?.ParentId;

        public IReadOnlyList<string> PreTags(int id, string field) =>
            Find(id) is { } item && item.Fields.TryGetValue(field, out var v) ? SplitTags(v) : [];

        public string? PostType(int id)
        {
            string? overlay = null;
            var touched = false;
            foreach (var batch in _plan.Operations.OfType<BatchOperation>().Where(b => b.WorkItemId == id))
            {
                foreach (var field in batch.Fields)
                {
                    if (Names.Equals(field.Key, TypeField))
                    {
                        overlay = field.Value;
                        touched = true;
                    }
                }
            }

            return touched ? (string.IsNullOrWhiteSpace(overlay) ? null : overlay) : PreType(id);
        }

        public IReadOnlyList<string> PostTags(int id, string field)
        {
            string? overlay = null;
            var touched = false;
            foreach (var batch in _plan.Operations.OfType<BatchOperation>().Where(b => b.WorkItemId == id))
            {
                foreach (var f in batch.Fields)
                {
                    if (Names.Equals(f.Key, field))
                    {
                        overlay = f.Value;
                        touched = true;
                    }
                }
            }

            return touched ? SplitTags(overlay) : PreTags(id, field);
        }

        public IEnumerable<ParentEdge> ParentEdges()
        {
            var removedParents = _plan.Operations.OfType<RemoveLinkOperation>()
                .Where(r => Names.Equals(r.Relation, ParentRelation))
                .Select(r => r.WorkItemId)
                .ToHashSet();

            foreach (var op in _plan.Operations)
            {
                if (op is AddLinkOperation add && Names.Equals(add.Relation, ParentRelation))
                    yield return new ParentEdge(add.WorkItemId, add.OtherId, add.Id);
            }

            foreach (var (seedId, opId) in _publishedSeedOps)
            {
                if (_seeds[seedId].ParentId is { } parent)
                    yield return new ParentEdge(seedId, parent, opId);
            }

            // A type change re-judges the item's existing parent edge: it may newly fall under a rule.
            foreach (var batch in _plan.Operations.OfType<BatchOperation>())
            {
                if (batch.Fields.Keys.Any(k => Names.Equals(k, TypeField))
                    && !removedParents.Contains(batch.WorkItemId)
                    && _cached.GetValueOrDefault(batch.WorkItemId)?.ParentId is { } parent)
                {
                    yield return new ParentEdge(batch.WorkItemId, parent, batch.Id);
                }
            }
        }

        public IEnumerable<LabelTarget> LabelTargets(string field)
        {
            var yielded = new HashSet<int>();
            foreach (var batch in _plan.Operations.OfType<BatchOperation>())
            {
                if (batch.Fields.Keys.Any(k => Names.Equals(k, field) || Names.Equals(k, TypeField))
                    && yielded.Add(batch.WorkItemId))
                {
                    yield return new LabelTarget(batch.WorkItemId, batch.Id, IsNewSeed: false);
                }
            }

            foreach (var (seedId, opId) in _publishedSeedOps)
            {
                if (yielded.Add(seedId))
                    yield return new LabelTarget(seedId, opId, IsNewSeed: true);
            }
        }
    }

    /// <summary>Dependency edges: verified cached links, plus proposed adds, minus proposed removes.</summary>
    private sealed class DependencyGraph(
        IWorkItemLinkRepository links,
        IEnumerable<(int From, int To)> adds,
        HashSet<(int, int)> removes)
    {
        private readonly ILookup<int, int> _added = adds.ToLookup(a => a.From, a => a.To);
        private readonly Dictionary<int, HashSet<int>?> _outgoing = [];

        /// <summary>True/false when the cache settles whether the edge exists; null when unverified.</summary>
        public async Task<bool?> CachedEdgeAsync(int from, int to, CancellationToken ct)
        {
            await LoadAsync([from], ct).ConfigureAwait(false);
            return _outgoing[from] is { } edges ? edges.Contains(to) : null;
        }

        public async Task<(bool Found, bool Truncated, IReadOnlyCollection<int> Unverified)> ReachesAsync(
            int start, int target, int maxNodes, CancellationToken ct)
        {
            var visited = new HashSet<int> { start };
            var unverified = new HashSet<int>();
            var frontier = new List<int> { start };
            while (frontier.Count > 0)
            {
                await LoadAsync(frontier, ct).ConfigureAwait(false);
                var next = new List<int>();
                foreach (var node in frontier)
                {
                    var cached = _outgoing[node];
                    if (cached is null)
                        unverified.Add(node);

                    var outgoing = (cached ?? []).Where(to => !removes.Contains((node, to))).Concat(_added[node]);
                    foreach (var to in outgoing)
                    {
                        if (to == target)
                            return (true, false, unverified);
                        if (!visited.Add(to))
                            continue;
                        if (visited.Count > maxNodes)
                            return (false, true, unverified);
                        next.Add(to);
                    }
                }

                frontier = next;
            }

            return (false, false, unverified);
        }

        private async Task LoadAsync(IReadOnlyList<int> nodes, CancellationToken ct)
        {
            var missing = nodes.Where(n => !_outgoing.ContainsKey(n)).Distinct().ToList();
            foreach (var seed in missing.Where(n => n < 0))
                _outgoing[seed] = [];
            missing.RemoveAll(n => n < 0);
            if (missing.Count == 0)
                return;

            var verified = await links.GetLinksVerifiedAtForSetAsync(missing, ct).ConfigureAwait(false);
            var found = await links.GetLinksForSetAsync(missing, ct).ConfigureAwait(false);
            foreach (var node in missing)
                _outgoing[node] = verified.ContainsKey(node) ? [] : null;

            foreach (var link in found)
            {
                (int From, int To)? edge = link.LinkType switch
                {
                    LinkTypes.Successor => (link.SourceId, link.TargetId),
                    LinkTypes.Predecessor => (link.TargetId, link.SourceId),
                    _ => null,
                };
                if (edge is { } e && _outgoing.TryGetValue(e.From, out var set) && set is not null)
                    set.Add(e.To);
            }
        }
    }
}
