using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;

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
/// Native cached repositories do not certify a complete child set for a retyped parent.
/// Such a change is unavailable when its final type is disallowed; explicitly named child
/// edges are still evaluated. Staged seed links are not part of the dependency graph.
/// </para>
/// </remarks>
public sealed class DeclaredPolicyEvaluator(IWorkItemRepository items, IWorkItemLinkRepository? links)
{
    internal const string TypeField = "System.WorkItemType";
    private const string ParentRelation = "parent";
    private const string PredecessorRelation = "predecessor";
    private const string SuccessorRelation = "successor";

    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private readonly IWorkItemSnapshotReader? _providerSnapshots = null;

    internal DeclaredPolicyEvaluator(
        IWorkItemRepository items,
        IWorkItemLinkRepository? links,
        IWorkItemSnapshotReader? providerSnapshots)
        : this(items, links)
    {
        _providerSnapshots = providerSnapshots;
    }

    /// <summary>Evaluates every rule in declared order.</summary>
    public Task<IReadOnlyList<PolicyDecision>> EvaluateAsync(
        DeclaredPolicy policy, PlanDefinition plan, CancellationToken ct = default)
        => EvaluateCoreAsync(policy, plan, null, ct);

    internal Task<IReadOnlyList<PolicyDecision>> EvaluateForExecutionAsync(
        DeclaredPolicy policy, PlanDefinition plan, IExecutionPolicyFactReader reader, CancellationToken ct)
        => EvaluateCoreAsync(policy, plan, new ExecutionFacts(reader), ct);

    private async Task<IReadOnlyList<PolicyDecision>> EvaluateCoreAsync(
        DeclaredPolicy policy, PlanDefinition plan, ExecutionFacts? facts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(plan);
        if (policy.Rules.Count == 0)
            return [];

        if (plan.Version == 2)
            return await EvaluateProviderAsync(policy, plan, ct).ConfigureAwait(false);

        var context = await ProposalState.LoadAsync(items, plan, ct, facts).ConfigureAwait(false);
        var decisions = new List<PolicyDecision>();
        foreach (var rule in policy.Rules)
        {
            var found = rule switch
            {
                ParentTypeRule r => EvaluateParentType(r, context),
                LabelApplicabilityRule r => EvaluateLabels(r, context),
                DependencyIntegrityRule r => await EvaluateDependenciesAsync(r, plan, facts, ct).ConfigureAwait(false),
                _ => [Decide(rule, PolicyOutcome.Unavailable, $"Rule kind '{rule.Kind}' is not supported by this Twig build.")],
            };
            decisions.AddRange(found);
        }

        return decisions;
    }

    private async Task<IReadOnlyList<PolicyDecision>> EvaluateProviderAsync(
        DeclaredPolicy policy, PlanDefinition plan, CancellationToken ct)
    {
        var state = new ProviderProposalState(_providerSnapshots, plan, ct);
        var decisions = new List<PolicyDecision>();
        foreach (var rule in policy.Rules)
        {
            var found = rule switch
            {
                ParentTypeRule r => await EvaluateProviderParentTypeAsync(r, state, ct).ConfigureAwait(false),
                LabelApplicabilityRule r => await EvaluateProviderLabelsAsync(r, state, ct).ConfigureAwait(false),
                DependencyIntegrityRule r => await EvaluateProviderDependenciesAsync(r, state, ct).ConfigureAwait(false),
                _ => [Decide(rule, PolicyOutcome.Unavailable, $"Rule kind '{rule.Kind}' is not supported by this Twig build.")],
            };
            decisions.AddRange(found);
        }

        return decisions;
    }

    private static async Task<List<PolicyDecision>> EvaluateProviderParentTypeAsync(
        ParentTypeRule rule, ProviderProposalState state, CancellationToken ct)
    {
        var findings = new List<PolicyDecision>();
        var judged = 0;
        var edges = state.ParentEdges().ToList();
        foreach (var retyped in await state.RetypedItemsAsync(rule.ChildType).ConfigureAwait(false))
        {
            var snapshot = await state.SnapshotAsync(retyped.Identity).ConfigureAwait(false);
            if (snapshot is not { RelationshipsComplete: true })
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Complete parent relationship facts for {Format(retyped.Identity)} are unavailable; "
                    + $"cannot re-evaluate its existing parent after the type change. {state.SnapshotFailure(retyped.Identity)}",
                    retyped.Identity, retyped.OpId));
                continue;
            }

            edges.AddRange(snapshot.Relationships
                .Where(r => r.Kind == LinkKind.ParentChild && r.Target == retyped.Identity
                    && !state.ParentRemoved(retyped.Identity, r.Source))
                .Select(r => new ProviderParentEdge(retyped.Identity, r.Source, retyped.OpId)));
        }

        foreach (var edge in edges)
        {
            var childType = await state.PostTypeAsync(edge.Child).ConfigureAwait(false);
            if (childType is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the child type for {Format(edge.Child)}. {state.SnapshotFailure(edge.Child)}",
                    edge.Child, edge.OpId));
                continue;
            }

            if (!Names.Equals(childType, rule.ChildType))
                continue;

            judged++;
            var parentType = await state.PostTypeAsync(edge.Parent).ConfigureAwait(false);
            if (parentType is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the parent type for {Format(edge.Parent)}. {state.SnapshotFailure(edge.Parent)}",
                    edge.Child, edge.OpId));
            }
            else if (!rule.AllowedParentTypes.Contains(parentType, Names))
            {
                findings.Add(ProviderDecide(rule, Violation(rule),
                    $"{rule.ChildType} {Format(edge.Child)} may not be parented under {parentType} "
                    + $"{Format(edge.Parent)}; allowed parent types: {string.Join(", ", rule.AllowedParentTypes)}.",
                    edge.Child, edge.OpId));
            }
        }

        return findings.Count > 0
            ? findings
            : [ProviderDecide(rule, PolicyOutcome.Permit,
                $"{judged} new {rule.ChildType} parent edge(s) checked; all parents are of an allowed type.")];
    }

    private static async Task<List<PolicyDecision>> EvaluateProviderLabelsAsync(
        LabelApplicabilityRule rule, ProviderProposalState state, CancellationToken ct)
    {
        var findings = new List<PolicyDecision>();
        var judged = 0;
        foreach (var target in state.LabelTargets(rule.Field))
        {
            judged++;
            var postTags = await state.PostTagsAsync(target.Identity, rule.Field).ConfigureAwait(false);
            if (postTags is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Label facts for {Format(target.Identity)} are unavailable or cannot be represented "
                    + $"without splitting a provider value. {state.SnapshotFailure(target.Identity)}",
                    target.Identity, target.OpId));
                continue;
            }

            if (!postTags.Any(t => HasPrefix(t, rule.Prefix)))
                continue;

            var postType = await state.PostTypeAsync(target.Identity).ConfigureAwait(false);
            if (postType is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the type of {Format(target.Identity)} carrying '{rule.Prefix}' labels.",
                    target.Identity, target.OpId, unavailableDetail: state.SnapshotFailure(target.Identity)));
                continue;
            }

            var after = LabelViolations(rule, postType, postTags);
            if (after.Count == 0)
                continue;

            var preTags = await state.PreTagsAsync(target.Identity, rule.Field).ConfigureAwait(false);
            var preType = await state.PreTypeAsync(target.Identity).ConfigureAwait(false);
            if (preTags is null || preType is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine whether the policy violation on {Format(target.Identity)} "
                    + $"was already present before this proposal. {state.SnapshotFailure(target.Identity)}",
                    target.Identity, target.OpId));
                continue;
            }

            var before = LabelViolations(rule, preType, preTags);
            var added = after.Where(v => !before.ContainsKey(v.Key)).ToList();
            if (added.Count == 0)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Warn,
                    $"Pre-existing, not worsened by this proposal: {string.Join("; ", after.Values)}.",
                    target.Identity, target.OpId));
                continue;
            }

            foreach (var violation in added)
                findings.Add(ProviderDecide(rule, Violation(rule), violation.Value, target.Identity, target.OpId));
        }

        return findings.Count > 0
            ? findings
            : [ProviderDecide(rule, PolicyOutcome.Permit,
                $"{judged} provider-observed item(s) with changed labels or type checked for '{rule.Prefix}' labels.")];
    }

    private static async Task<List<PolicyDecision>> EvaluateProviderDependenciesAsync(
        DependencyIntegrityRule rule, ProviderProposalState state, CancellationToken ct)
    {
        var findings = new List<PolicyDecision>();
        var adds = new List<ProviderDependencyAdd>();
        var removes = state.DependencyRemoves();
        foreach (var add in state.DependencyAdds())
        {
            if (add.From == add.To)
            {
                findings.Add(ProviderDecide(rule, Violation(rule),
                    $"{Format(add.From)} cannot be linked to itself as a dependency.", add.Target, add.OpId));
                continue;
            }

            adds.Add(add);
        }

        if (adds.Count == 0)
            return findings.Count > 0
                ? findings
                : [ProviderDecide(rule, PolicyOutcome.Permit, "No new provider dependency edges.")];

        var graph = new ProviderDependencyGraph(state, adds.Select(a => (a.From, a.To)));
        var seen = new HashSet<(ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)>();
        foreach (var add in adds)
        {
            var edge = (add.From, add.To);
            if (!seen.Add(edge))
            {
                findings.Add(ProviderDecide(rule, Violation(rule),
                    $"Dependency {Format(add.From)} → {Format(add.To)} is declared more than once in this proposal.",
                    add.Target, add.OpId));
                continue;
            }

            var cached = await graph.CachedEdgeAsync(add.From, add.To, ct).ConfigureAwait(false);
            if (cached is null)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Complete relationship facts for {Format(add.From)} are unavailable; "
                    + $"cannot check {Format(add.From)} → {Format(add.To)}.",
                    add.Target, add.OpId, state.SnapshotFailure(add.From)));
                continue;
            }

            if (cached.Value)
            {
                findings.Add(ProviderDecide(rule, Violation(rule),
                    $"Dependency {Format(add.From)} → {Format(add.To)} already exists.",
                    add.Target, add.OpId));
                continue;
            }

            var search = await graph.ReachesAsync(add.To, add.From, removes, rule.MaxNodes, ct)
                .ConfigureAwait(false);
            if (search.Found)
            {
                findings.Add(ProviderDecide(rule, Violation(rule),
                    $"Dependency {Format(add.From)} → {Format(add.To)} would create a cycle: "
                    + $"{Format(add.To)} already leads back to {Format(add.From)}.", add.Target, add.OpId));
            }
            else if (search.Truncated)
            {
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cycle check for {Format(add.From)} → {Format(add.To)} exceeded {rule.MaxNodes} items.",
                    add.Target, add.OpId));
            }
            else if (search.Unverified.Count > 0)
            {
                var identities = string.Join(", ", search.Unverified.Take(10).Select(Format));
                findings.Add(ProviderDecide(rule, PolicyOutcome.Unavailable,
                    $"Cycle check reached items without complete relationship facts ({identities}).",
                    add.Target, add.OpId));
            }
        }

        return findings.Count > 0
            ? findings
            : [ProviderDecide(rule, PolicyOutcome.Permit,
                $"{adds.Count} provider-observed dependency edge(s) checked: no self-links, duplicates or cycles.")];
    }

    private static PolicyDecision ProviderDecide(
        DeclaredPolicyRule rule,
        PolicyOutcome outcome,
        string reason,
        ExternalWorkItemIdentity? identity = null,
        string? opId = null,
        string? unavailableDetail = null)
    {
        var subject = identity is null ? string.Empty : $" for {Format(identity)}";
        var detail = unavailableDetail is null ? string.Empty : $" {unavailableDetail}";
        return Decide(rule, outcome,
            $"Observed at preview{subject}; this is not exact-revision apply authority. {reason}{detail}",
            opId: opId,
            providerIdentity: identity);
    }

    private static string Format(ExternalWorkItemIdentity identity) =>
        $"{identity.ProviderKind} item '{identity.NativeId}' in '{identity.ResourceScope}'";

    private static (ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)? NormalizeProvider(
        string relation, ExternalWorkItemIdentity target, ExternalWorkItemIdentity? other)
    {
        if (other is null)
            return null;
        if (Names.Equals(relation, PredecessorRelation))
            return (other, target);
        if (Names.Equals(relation, SuccessorRelation))
            return (target, other);
        return null;
    }

    private readonly record struct ProviderParentEdge(
        ExternalWorkItemIdentity Child, ExternalWorkItemIdentity Parent, string OpId);

    private readonly record struct ProviderRetypedItem(ExternalWorkItemIdentity Identity, string OpId);

    private readonly record struct ProviderLabelTarget(ExternalWorkItemIdentity Identity, string OpId);

    private readonly record struct ProviderDependencyAdd(
        ExternalWorkItemIdentity From,
        ExternalWorkItemIdentity To,
        ExternalWorkItemIdentity Target,
        string OpId);

    /// <summary>
    /// Provider-qualified acquisition for the same declared rules. It deliberately exposes
    /// observations only: a nullable token or complete graph read is never apply authority.
    /// </summary>
    private sealed class ProviderProposalState(
        IWorkItemSnapshotReader? reader, PlanDefinition plan, CancellationToken ct)
    {
        private readonly Dictionary<ExternalWorkItemIdentity, Task<ProviderWorkItemSnapshot?>> _snapshots = [];
        private readonly Dictionary<ExternalWorkItemIdentity, string> _snapshotFailures = [];

        public IEnumerable<ProviderParentEdge> ParentEdges()
        {
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind == PlanOperationKind.AddLink
                    && Names.Equals(operation.Relation, ParentRelation)
                    && operation.Other is { } parent)
                {
                    yield return new(operation.Target.Item.ExternalIdentity, parent.ExternalIdentity, operation.Id);
                }
            }
        }

        public async Task<IReadOnlyList<ProviderRetypedItem>> RetypedItemsAsync(string childType)
        {
            var retyped = new Dictionary<ExternalWorkItemIdentity, string>();
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind == PlanOperationKind.Batch
                    && operation.Fields?.Keys.Any(k => Names.Equals(k, TypeField)) == true)
                    retyped[operation.Target.Item.ExternalIdentity] = operation.Id;
            }

            var result = new List<ProviderRetypedItem>();
            foreach (var (identity, opId) in retyped)
                if (Names.Equals(await PostTypeAsync(identity).ConfigureAwait(false), childType))
                    result.Add(new(identity, opId));
            return result;
        }

        public bool ParentRemoved(ExternalWorkItemIdentity child, ExternalWorkItemIdentity parent) =>
            plan.Operations.OfType<ProviderMutationOperation>().Any(operation =>
                operation.OperationKind == PlanOperationKind.RemoveLink
                && operation.Target.Item.ExternalIdentity == child
                && operation.Other?.ExternalIdentity == parent
                && Names.Equals(operation.Relation, ParentRelation));

        public IEnumerable<ProviderLabelTarget> LabelTargets(string field)
        {
            var yielded = new HashSet<ExternalWorkItemIdentity>();
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind != PlanOperationKind.Batch || operation.Fields is null)
                    continue;

                if (operation.Fields.Keys.Any(k => Names.Equals(k, field) || Names.Equals(k, TypeField))
                    && yielded.Add(operation.Target.Item.ExternalIdentity))
                {
                    yield return new(operation.Target.Item.ExternalIdentity, operation.Id);
                }
            }
        }

        public IEnumerable<ProviderDependencyAdd> DependencyAdds()
        {
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind != PlanOperationKind.AddLink
                    || operation.Relation is null || operation.Other is null)
                    continue;

                if (NormalizeProvider(operation.Relation, operation.Target.Item.ExternalIdentity,
                    operation.Other.ExternalIdentity) is { } edge)
                {
                    yield return new(edge.From, edge.To, operation.Target.Item.ExternalIdentity, operation.Id);
                }
            }
        }

        public HashSet<(ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)> DependencyRemoves()
        {
            var removes = new HashSet<(ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)>();
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind == PlanOperationKind.RemoveLink
                    && operation.Relation is not null && operation.Other is not null
                    && NormalizeProvider(operation.Relation, operation.Target.Item.ExternalIdentity,
                        operation.Other.ExternalIdentity) is { } edge)
                {
                    removes.Add(edge);
                }
            }

            return removes;
        }

        public async Task<string?> PostTypeAsync(ExternalWorkItemIdentity identity)
        {
            if (TryGetOverlay(identity, TypeField, out var overlay))
                return string.IsNullOrWhiteSpace(overlay) ? null : overlay;

            var snapshot = await SnapshotAsync(identity).ConfigureAwait(false);
            return snapshot is not null && TryGetField(snapshot.Fields, TypeField, out var value)
                && value.Value is TextValue text && !string.IsNullOrWhiteSpace(text.Value)
                    ? text.Value : null;
        }

        public async Task<string?> PreTypeAsync(ExternalWorkItemIdentity identity)
        {
            var snapshot = await SnapshotAsync(identity).ConfigureAwait(false);
            return snapshot is not null && TryGetField(snapshot.Fields, TypeField, out var value)
                && value.Value is TextValue text && !string.IsNullOrWhiteSpace(text.Value)
                    ? text.Value : null;
        }

        public async Task<IReadOnlyList<string>?> PostTagsAsync(
            ExternalWorkItemIdentity identity, string field)
        {
            if (TryGetOverlay(identity, field, out var overlay))
            {
                if (overlay is null)
                    return [];
                if (overlay.Contains(';'))
                    return null;
                return overlay.Length == 0 ? [] : [overlay];
            }

            return ReadTags(await SnapshotAsync(identity).ConfigureAwait(false), field);
        }

        public async Task<IReadOnlyList<string>?> PreTagsAsync(
            ExternalWorkItemIdentity identity, string field) =>
            ReadTags(await SnapshotAsync(identity).ConfigureAwait(false), field);

        public async Task<ProviderWorkItemSnapshot?> SnapshotAsync(
            ExternalWorkItemIdentity identity)
        {
            if (!_snapshots.TryGetValue(identity, out var read))
            {
                read = ReadAsync(identity);
                _snapshots.Add(identity, read);
            }

            return await read.ConfigureAwait(false);
        }

        public string SnapshotFailure(ExternalWorkItemIdentity identity) =>
            _snapshotFailures.TryGetValue(identity, out var failure)
                ? failure
                : "Provider snapshot facts are unavailable.";

        private async Task<ProviderWorkItemSnapshot?> ReadAsync(ExternalWorkItemIdentity identity)
        {
            if (reader is null)
            {
                _snapshotFailures[identity] = "No provider snapshot reader is configured.";
                return null;
            }

            try
            {
                var snapshot = await reader.FetchSnapshotAsync(identity, ct).ConfigureAwait(false);
                if (snapshot.Identity == identity)
                    return snapshot;

                _snapshotFailures[identity] = "The provider returned a snapshot for a different identity.";
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                _snapshotFailures[identity] = "The provider request failed.";
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                _snapshotFailures[identity] = "The provider response was incomplete or invalid.";
                return null;
            }
            catch (Exception)
            {
                _snapshotFailures[identity] = "The provider read failed.";
                return null;
            }
        }

        private bool TryGetOverlay(ExternalWorkItemIdentity identity, string field, out string? value)
        {
            var found = false;
            value = null;
            foreach (var operation in plan.Operations.OfType<ProviderMutationOperation>())
            {
                if (operation.OperationKind != PlanOperationKind.Batch
                    || operation.Fields is null
                    || operation.Target.Item.ExternalIdentity != identity)
                    continue;

                foreach (var change in operation.Fields)
                {
                    if (Names.Equals(change.Key, field))
                    {
                        found = true;
                        value = change.Value;
                    }
                }
            }

            return found;
        }

        private static IReadOnlyList<string>? ReadTags(
            ProviderWorkItemSnapshot? snapshot, string field)
        {
            if (snapshot is null || !TryGetField(snapshot.Fields, field, out var value))
                return null;

            return value.Value switch
            {
                StringListValue list => list.Values,
                NullValue => [],
                TextValue text when !text.Value.Contains(';') =>
                    text.Value.Length == 0 ? [] : [text.Value],
                _ => null,
            };
        }

        private static bool TryGetField(FieldValues fields, string name, out FieldValue value)
        {
            foreach (var field in fields)
            {
                if (Names.Equals(field.Key, name))
                {
                    value = field.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }

    private sealed class ProviderDependencyGraph(
        ProviderProposalState state,
        IEnumerable<(ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)> adds)
    {
        private readonly ILookup<ExternalWorkItemIdentity, ExternalWorkItemIdentity> _added =
            adds.ToLookup(a => a.From, a => a.To);
        private readonly Dictionary<ExternalWorkItemIdentity, HashSet<ExternalWorkItemIdentity>?> _outgoing = [];

        public async Task<bool?> CachedEdgeAsync(
            ExternalWorkItemIdentity from, ExternalWorkItemIdentity to, CancellationToken ct)
        {
            await LoadAsync([from], ct).ConfigureAwait(false);
            return _outgoing[from] is { } edges ? edges.Contains(to) : null;
        }

        public async Task<(bool Found, bool Truncated, IReadOnlyCollection<ExternalWorkItemIdentity> Unverified)>
            ReachesAsync(
                ExternalWorkItemIdentity start,
                ExternalWorkItemIdentity target,
                HashSet<(ExternalWorkItemIdentity From, ExternalWorkItemIdentity To)> removes,
                int maxNodes,
                CancellationToken ct)
        {
            var visited = new HashSet<ExternalWorkItemIdentity> { start };
            var unverified = new HashSet<ExternalWorkItemIdentity>();
            var frontier = new List<ExternalWorkItemIdentity> { start };
            while (frontier.Count > 0)
            {
                await LoadAsync(frontier, ct).ConfigureAwait(false);
                var next = new List<ExternalWorkItemIdentity>();
                foreach (var node in frontier)
                {
                    var cached = _outgoing[node];
                    if (cached is null)
                        unverified.Add(node);

                    var outgoing = (cached ?? [])
                        .Where(to => !removes.Contains((node, to)))
                        .Concat(_added[node]);
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

        private async Task LoadAsync(IReadOnlyList<ExternalWorkItemIdentity> nodes, CancellationToken ct)
        {
            var missing = nodes.Where(n => !_outgoing.ContainsKey(n)).Distinct().ToList();
            foreach (var identity in missing)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = await state.SnapshotAsync(identity).ConfigureAwait(false);
                _outgoing[identity] = snapshot is { RelationshipsComplete: true }
                    ? snapshot.Relationships
                        .Where(r => r.Kind == LinkKind.PredecessorSuccessor && r.Source == identity)
                        .Select(r => r.Target)
                        .ToHashSet()
                    : null;
            }
        }
    }

    private static List<PolicyDecision> EvaluateParentType(ParentTypeRule rule, ProposalState state)
    {
        var findings = new List<PolicyDecision>();
        var judged = 0;
        var retypedParents = state.RetypedParents().ToList();
        foreach (var parent in retypedParents)
        {
            var parentType = state.PostType(parent.ParentId);
            if (parentType is null)
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Cannot determine the final type of retyped parent #{parent.ParentId}; "
                    + "complete native child relationship facts are unavailable.",
                    parent.ParentId, parent.OpId));
            }
            else if (!rule.AllowedParentTypes.Contains(parentType, Names))
            {
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Complete native child relationship facts for retyped parent #{parent.ParentId} "
                    + $"are unavailable; cannot certify whether {rule.ChildType} children remain under "
                    + $"the disallowed type {parentType}.",
                    parent.ParentId, parent.OpId));
            }
        }

        foreach (var edge in state.ParentEdges(retypedParents))
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
                if (!retypedParents.Any(parent => parent.ParentId == edge.ParentId))
                {
                    findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                        $"Cannot determine the type of parent #{edge.ParentId} of {rule.ChildType} #{edge.ChildId}; refresh it with 'twig show {edge.ParentId} --refresh'.",
                        edge.ChildId, edge.OpId));
                }
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
        DependencyIntegrityRule rule, PlanDefinition plan, ExecutionFacts? facts, CancellationToken ct)
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

        if (links is null && facts is null)
        {
            foreach (var add in adds)
                findings.Add(Decide(rule, PolicyOutcome.Unavailable,
                    $"Dependency links are not available to check #{add.From} → #{add.To}.", add.Source, add.OpId));
            return findings;
        }

        var graph = new DependencyGraph(links, adds.Select(a => (a.From, a.To)), removes, facts);
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
        DeclaredPolicyRule rule,
        PolicyOutcome outcome,
        string reason,
        int? workItemId = null,
        string? opId = null,
        ExternalWorkItemIdentity? providerIdentity = null) =>
        new()
        {
            RuleId = rule.Id,
            Kind = rule.Kind,
            Outcome = outcome,
            Reason = reason,
            WorkItemId = workItemId,
            OpId = opId,
            ProviderIdentity = providerIdentity,
        };

    internal static IReadOnlyList<string> SplitTags(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly record struct ParentEdge(int ChildId, int ParentId, string OpId);

    private readonly record struct ParentRetype(int ParentId, string OpId);

    private readonly record struct LabelTarget(int Id, string OpId, bool IsNewSeed);

    private sealed class ExecutionFacts(IExecutionPolicyFactReader reader)
    {
        private readonly Dictionary<int, WorkItem> _items = [];
        private readonly Dictionary<int, IReadOnlyList<WorkItemLink>> _links = [];

        public async Task<IReadOnlyList<WorkItem>> ItemsAsync(IEnumerable<int> ids, CancellationToken ct)
        {
            var requested = ids.Distinct().ToArray();
            var missing = requested.Where(id => !_items.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                var observed = await reader.FetchPolicyFactsAsync(missing, ct).ConfigureAwait(false);
                if (observed.Items is null || observed.Links is null
                    || observed.Items.Count != missing.Length
                    || observed.Items.Select(item => item.Id).Distinct().Count() != missing.Length
                    || observed.Items.Any(item => !missing.Contains(item.Id)
                        || item.Revision <= 0 || string.IsNullOrWhiteSpace(item.Type.Value))
                    || observed.Links.Any(link => link.SourceId <= 0 || link.TargetId <= 0
                        || (!missing.Contains(link.SourceId) && !missing.Contains(link.TargetId))))
                    throw new InvalidOperationException("Current native policy facts are incomplete or inconsistent.");

                foreach (var item in observed.Items)
                {
                    _items.Add(item.Id, item);
                    _links.Add(item.Id, observed.Links
                        .Where(link => link.SourceId == item.Id || link.TargetId == item.Id)
                        .ToArray());
                }
            }
            return requested.Select(id => _items[id]).ToArray();
        }

        public async Task<IReadOnlyList<WorkItemLink>> LinksAsync(
            IReadOnlyList<int> ids, CancellationToken ct)
        {
            await ItemsAsync(ids, ct).ConfigureAwait(false);
            return ids.SelectMany(id => _links[id]).Distinct().ToArray();
        }
    }

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

        public static async Task<ProposalState> LoadAsync(
            IWorkItemRepository items, PlanDefinition plan, CancellationToken ct, ExecutionFacts? facts = null)
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
                    case AppendCommentOperation a: ids.Add(a.WorkItemId); break;
                }
            }

            foreach (var seedId in published.Keys)
            {
                if (seeds[seedId].ParentId is { } parent)
                    ids.Add(parent);
            }

            ids.RemoveWhere(i => i <= 0);
            IReadOnlyList<WorkItem> observed = ids.Count == 0
                ? []
                : facts is null
                    ? await items.GetByIdsAsync(ids, ct).ConfigureAwait(false)
                    : await facts.ItemsAsync(ids, ct).ConfigureAwait(false);
            var cached = observed.ToDictionary(w => w.Id);

            // A type change re-judges the item's existing parent, which no operation names.
            var parentsOfRetyped = plan.Operations.OfType<BatchOperation>()
                .Where(b => b.Fields.Keys.Any(k => Names.Equals(k, TypeField)))
                .Select(b => cached.GetValueOrDefault(b.WorkItemId)?.ParentId)
                .OfType<int>()
                .Where(p => p > 0 && !cached.ContainsKey(p))
                .ToHashSet();
            if (parentsOfRetyped.Count > 0)
            {
                var parents = facts is null
                    ? await items.GetByIdsAsync(parentsOfRetyped, ct).ConfigureAwait(false)
                    : await facts.ItemsAsync(parentsOfRetyped, ct).ConfigureAwait(false);
                foreach (var parent in parents)
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

        public IEnumerable<ParentRetype> RetypedParents()
        {
            var deletedItems = _plan.Operations.OfType<DeleteOperation>()
                .Select(d => d.WorkItemId)
                .ToHashSet();
            foreach (var batch in _plan.Operations.OfType<BatchOperation>()
                .Where(b => b.Fields.Keys.Any(k => Names.Equals(k, TypeField)))
                .Where(b => !deletedItems.Contains(b.WorkItemId))
                .GroupBy(b => b.WorkItemId)
                .Select(group => group.Last()))
            {
                var previousType = PreType(batch.WorkItemId);
                var finalType = PostType(batch.WorkItemId);
                if (previousType is null || finalType is null || !Names.Equals(previousType, finalType))
                    yield return new ParentRetype(batch.WorkItemId, batch.Id);
            }
        }

        public IEnumerable<ParentEdge> ParentEdges(IReadOnlyList<ParentRetype> retypedParents)
        {
            var removedParents = _plan.Operations.OfType<RemoveLinkOperation>()
                .Where(r => Names.Equals(r.Relation, ParentRelation))
                .Select(r => (ChildId: r.WorkItemId, ParentId: r.OtherId))
                .ToHashSet();
            var deletedItems = _plan.Operations.OfType<DeleteOperation>()
                .Select(d => d.WorkItemId)
                .ToHashSet();
            var yielded = new HashSet<(int ChildId, int ParentId)>();

            foreach (var op in _plan.Operations)
            {
                if (op is AddLinkOperation add && Names.Equals(add.Relation, ParentRelation)
                    && yielded.Add((add.WorkItemId, add.OtherId)))
                {
                    yield return new ParentEdge(add.WorkItemId, add.OtherId, add.Id);
                }
            }

            foreach (var (seedId, opId) in _publishedSeedOps)
            {
                if (_seeds[seedId].ParentId is { } parent && yielded.Add((seedId, parent)))
                {
                    yield return new ParentEdge(seedId, parent, opId);
                }
            }

            // Native repository reads expose cached items, not a complete child-set snapshot.
            // Re-evaluate only proposal-named children; EvaluateParentType separately reports
            // unavailable for a disallowed parent type because other children may be uncached.
            foreach (var retypedParent in retypedParents)
            {
                foreach (var childId in _plan.Operations
                    .Select(TargetItemId)
                    .Where(id => id > 0)
                    .Distinct())
                {
                    if (!removedParents.Contains((childId, retypedParent.ParentId))
                        && !deletedItems.Contains(childId)
                        && _cached.GetValueOrDefault(childId)?.ParentId == retypedParent.ParentId
                        && yielded.Add((childId, retypedParent.ParentId)))
                    {
                        yield return new ParentEdge(childId, retypedParent.ParentId, retypedParent.OpId);
                    }
                }
            }

            // A type change re-judges the item's existing parent edge: it may newly fall under a rule.
            foreach (var batch in _plan.Operations.OfType<BatchOperation>())
            {
                if (batch.Fields.Keys.Any(k => Names.Equals(k, TypeField))
                    && !deletedItems.Contains(batch.WorkItemId)
                    && _cached.GetValueOrDefault(batch.WorkItemId)?.ParentId is { } parent)
                {
                    if (!removedParents.Contains((batch.WorkItemId, parent))
                        && yielded.Add((batch.WorkItemId, parent)))
                        yield return new ParentEdge(batch.WorkItemId, parent, batch.Id);
                }
            }
        }

        private static int TargetItemId(PlanOperationDefinition operation) => operation switch
        {
            BatchOperation batch => batch.WorkItemId,
            AddLinkOperation add => add.WorkItemId,
            RemoveLinkOperation remove => remove.WorkItemId,
            AppendCommentOperation append => append.WorkItemId,
            _ => 0,
        };

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
        IWorkItemLinkRepository? links,
        IEnumerable<(int From, int To)> adds,
        HashSet<(int, int)> removes,
        ExecutionFacts? facts)
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

            IReadOnlyList<WorkItemLink> found;
            if (facts is not null)
            {
                found = await facts.LinksAsync(missing, ct).ConfigureAwait(false);
                foreach (var node in missing)
                    _outgoing[node] = [];
            }
            else
            {
                var repository = links
                    ?? throw new InvalidOperationException("Dependency link observations are unavailable.");
                var verified = await repository.GetLinksVerifiedAtForSetAsync(missing, ct).ConfigureAwait(false);
                found = await repository.GetLinksForSetAsync(missing, ct).ConfigureAwait(false);
                foreach (var node in missing)
                    _outgoing[node] = verified.ContainsKey(node) ? [] : null;
            }

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
