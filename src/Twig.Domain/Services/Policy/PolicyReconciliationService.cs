using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Policy;

/// <summary>
/// Assesses cached current facts and renders explicit repairs as native proposal operations.
/// Never writes work items, resolves merge conflicts, clears process fields, or settles attempts.
/// </summary>
internal sealed class PolicyReconciliationService(IWorkItemRepository items, IWorkItemLinkRepository? links)
{
    public const int MaxScope = 200;
    public const int MaxGraphNodes = 1000;
    public const int MaxGraphEdges = 4000;
    public const int MaxStrategies = 32;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public async Task<PolicyAssessment> AssessAsync(
        DeclaredPolicy policy, IReadOnlyList<int> ids, CancellationToken ct = default)
    {
        using var admission = items.AcquireOperation();
        var snapshot = await CaptureAsync(policy, ids, ct).ConfigureAwait(false);
        return Assess(policy, snapshot);
    }

    public async Task<PolicyRepairResult> ProposeAsync(
        DeclaredPolicy policy, IReadOnlyList<int> ids, PolicyRepairSelection selection,
        PlanWorkspace workspace, CancellationToken ct = default)
    {
        using var admission = items.AcquireOperation();
        var snapshot = await CaptureAsync(policy, ids, ct).ConfigureAwait(false);
        var before = Assess(policy, snapshot);
        PolicyRepairResult Refuse(string reason, PolicyAssessment? projected = null) =>
            new() { Before = before, Projected = projected, Error = reason };
        if (before.EvidenceDigest != selection.EvidenceDigest)
            return Refuse("Assessed evidence or policy changed. Assess again and explicitly select a current alternative.");
        if (!before.Complete)
            return Refuse("Evidence is incomplete. Refresh the named items/links and assess again; no repair was rendered.");
        var finding = before.Findings.FirstOrDefault(f => f.Id == selection.FindingId);
        var strategy = finding?.Strategies.FirstOrDefault(s => s.Id == selection.StrategyId);
        if (finding is null || strategy is null)
            return Refuse("The selected finding/strategy is not part of this assessment.");
        if (strategy.RequiresParentId != selection.ParentId.HasValue)
            return Refuse(strategy.RequiresParentId
                ? "This strategy requires an explicit --parent from the assessed scope."
                : "--parent is only valid for the reparent strategy.");

        var operations = new List<PlanOperationDefinition>();
        var projectedSnapshot = snapshot.Copy();
        var item = snapshot.Cached[finding.WorkItemId];
        if (finding.Code == PolicyFindingCode.ParentType)
        {
            if (selection.ParentId is { } parent)
            {
                if (!snapshot.Scope.Contains(parent) || !snapshot.Cached.TryGetValue(parent, out var target)
                    || !Usable(target) || parent == item.Id)
                    return Refuse("The explicit parent must be a different, synchronized item included in --ids.");
                if (!finding.Evidence.AllowedValues.Contains(target.Type.Value, Names))
                    return Refuse("The selected parent has a type disallowed by this rule.");
                var visited = new HashSet<int> { item.Id };
                int? next = parent;
                while (next is { } current)
                {
                    if (!visited.Add(current))
                        return Refuse("The selected parent would create a hierarchy cycle.");
                    if (!snapshot.Cached.TryGetValue(current, out var ancestor) || !Usable(ancestor))
                        return Refuse("The parent ancestry is incomplete. Include its ancestors in --ids and assess again.");
                    next = ancestor.ParentId;
                }
            }
            var oldParent = finding.Evidence.ParentId!.Value;
            operations.Add(new RemoveLinkOperation
            {
                Id = "remove-parent", WorkItemId = item.Id, ExpectedRevision = item.Revision,
                Relation = "parent", OtherId = oldParent,
            });
            if (selection.ParentId is { } targetId)
                operations.Add(new AddLinkOperation
                {
                    Id = "add-parent", WorkItemId = item.Id, ExpectedRevision = checked(item.Revision + 1),
                    Relation = "parent", OtherId = targetId,
                });
            projectedSnapshot.Parents[item.Id] = selection.ParentId;
        }
        else if (finding.Code is PolicyFindingCode.LabelType or PolicyFindingCode.ExclusiveLabels)
        {
            var field = finding.Evidence.Field!;
            var rule = (LabelApplicabilityRule)policy.Rules.Single(r => r.Id == finding.RuleId);
            var labels = DeclaredPolicyEvaluator.SplitTags(item.Fields[field]);
            var retained = false;
            var kept = new List<string>();
            foreach (var label in labels)
            {
                if (!label.StartsWith(rule.Prefix, StringComparison.OrdinalIgnoreCase))
                    kept.Add(label);
                else if (!retained && Names.Equals(label, strategy.RetainLabel))
                {
                    kept.Add(label);
                    retained = true;
                }
            }
            var value = string.Join("; ", kept);
            operations.Add(new BatchOperation
            {
                Id = "repair-labels", WorkItemId = item.Id, ExpectedRevision = item.Revision,
                Fields = new Dictionary<string, string?> { [field] = value },
            });
            projectedSnapshot.Fields[item.Id] = new(StringComparer.OrdinalIgnoreCase) { [field] = value };
        }
        else if (strategy.RemoveEdge is { } edge)
        {
            var source = snapshot.RawLinks.First(l => Normalize(l) == edge);
            var sourceItem = snapshot.Cached[source.SourceId];
            operations.Add(new RemoveLinkOperation
            {
                Id = "remove-dependency", WorkItemId = source.SourceId, ExpectedRevision = sourceItem.Revision,
                Relation = source.LinkType.ToLowerInvariant(), OtherId = source.TargetId,
            });
            projectedSnapshot.RawLinks.RemoveAll(l => Normalize(l) == edge);
        }
        else
            return Refuse("This finding requires evidence acquisition, not an automatic repair.");

        var projected = Assess(policy, projectedSnapshot);
        if (!projected.Complete)
            return Refuse("The repair's effects could not be fully assessed.", projected);
        if (projected.Findings.Any(after => !before.Findings.Any(prior => Covers(prior, after))
            && !(after.Code == PolicyFindingCode.DependencyCycle
                && after.Evidence.Edges.All(edge => snapshot.RawLinks.Any(link => Normalize(link) == edge)))))
            return Refuse("The selected repair introduces or worsens a violation of another declared rule.", projected);
        if (projected.Findings.Any(after => Covers(after, finding)))
            return Refuse("This alternative does not improve the selected finding.", projected);

        var fresh = Assess(policy, await CaptureAsync(policy, ids, ct).ConfigureAwait(false));
        if (fresh.EvidenceDigest != before.EvidenceDigest)
            return Refuse("Cached evidence changed while rendering. Assess again; no proposal was emitted.");
        return new PolicyRepairResult
        {
            Before = before, Projected = projected,
            Proposal = new PlanDefinition { Version = 1, Workspace = workspace, Operations = operations },
        };
    }

    private static bool Covers(PolicyFinding prior, PolicyFinding after) =>
        prior.RuleId == after.RuleId && prior.Code == after.Code && prior.WorkItemId == after.WorkItemId
        && prior.Evidence.ParentId == after.Evidence.ParentId
        && prior.Evidence.Count >= after.Evidence.Count
        && after.Evidence.Labels.All(label => prior.Evidence.Labels.Contains(label, Names))
        && after.Evidence.Edges.All(prior.Evidence.Edges.Contains);

    private async Task<Snapshot> CaptureAsync(DeclaredPolicy policy, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count is 0 or > MaxScope || ids.Any(id => id <= 0))
            throw new ArgumentException($"Name 1 to {MaxScope} published positive item IDs.");
        var snapshot = new Snapshot(ids.Distinct().Order().ToArray());
        await LoadItemsAsync(snapshot, snapshot.Scope, ct).ConfigureAwait(false);
        var parents = snapshot.Cached.Values.Select(w => w.ParentId).OfType<int>().Distinct().ToArray();
        await LoadItemsAsync(snapshot, parents, ct).ConfigureAwait(false);
        if (!policy.Rules.Any(r => r is DependencyIntegrityRule))
            return snapshot;

        var bound = policy.Rules.OfType<DependencyIntegrityRule>().Max(r => Math.Min(r.MaxNodes, MaxGraphNodes));
        var visited = snapshot.Scope.Take(bound).ToHashSet();
        snapshot.Truncated = snapshot.Scope.Count > bound;
        var frontier = visited.Order().ToArray();
        while (frontier.Length > 0)
        {
            ct.ThrowIfCancellationRequested();
            await LoadItemsAsync(snapshot, frontier, ct).ConfigureAwait(false);
            snapshot.GraphNodes.UnionWith(frontier);
            if (links is null)
                break;
            var stamps = await links.GetLinksVerifiedAtForSetAsync(frontier, ct).ConfigureAwait(false);
            foreach (var (id, stamp) in stamps)
                snapshot.Verified[id] = stamp;
            var found = await links.GetLinksForSetAsync(frontier, ct).ConfigureAwait(false);
            var next = new SortedSet<int>();
            void Discover(int id)
            {
                if (visited.Contains(id))
                    return;
                if (visited.Count >= bound)
                    snapshot.Truncated = true;
                else
                {
                    visited.Add(id);
                    next.Add(id);
                }
            }
            var incoming = await links.GetIncomingDependencySourcesAsync(frontier, bound + 1, ct).ConfigureAwait(false);
            if (incoming.Count > bound)
                snapshot.Truncated = true;
            foreach (var source in incoming)
            {
                snapshot.IncomingSources.Add(source);
                Discover(source);
            }
            foreach (var link in found.Where(l => frontier.Contains(l.SourceId)))
            {
                if (Normalize(link) is null)
                    continue;
                if (snapshot.RawLinks.Count >= MaxGraphEdges)
                {
                    snapshot.Truncated = true;
                    break;
                }
                snapshot.RawLinks.Add(link);
                Discover(link.TargetId);
            }
            frontier = next.ToArray();
        }
        return snapshot;
    }

    private async Task LoadItemsAsync(Snapshot snapshot, IEnumerable<int> ids, CancellationToken ct)
    {
        var missing = ids.Where(id => !snapshot.Cached.ContainsKey(id)).Distinct().ToArray();
        if (missing.Length == 0) return;
        foreach (var item in await items.GetByIdsAsync(missing, ct).ConfigureAwait(false))
            snapshot.Cached[item.Id] = item;
    }

    private static bool Usable(WorkItem item) =>
        item.Revision > 0 && item.LastSyncedAt is not null && !item.IsDirty && !string.IsNullOrWhiteSpace(item.Type.Value);

    private static PolicyAssessment Assess(DeclaredPolicy policy, Snapshot snapshot)
    {
        var findings = new List<PolicyFinding>();
        void Add(DeclaredPolicyRule rule, PolicyFindingCode code, int id, string reason,
            PolicyFindingEvidence? evidence = null, IEnumerable<PolicyRepairStrategy>? strategies = null)
        {
            var options = strategies?.ToArray() ?? [];
            findings.Add(new PolicyFinding
            {
                Id = $"f{findings.Count + 1}", RuleId = rule.Id, Kind = rule.Kind, Code = code,
                WorkItemId = id, Reason = reason, Evidence = evidence ?? new(),
                Strategies = options.Take(MaxStrategies).ToArray(), StrategiesTruncated = options.Length > MaxStrategies,
            });
        }
        void Unavailable(DeclaredPolicyRule rule, int id, string reason, params int[] missing) =>
            Add(rule, PolicyFindingCode.EvidenceUnavailable, id, reason,
                new() { MissingIds = missing });

        foreach (var rule in policy.Rules)
        {
            if (rule is DependencyIntegrityRule dependency)
            {
                var bound = Math.Min(dependency.MaxNodes, MaxGraphNodes);
                var missing = snapshot.GraphNodes.Where(id => !snapshot.Verified.ContainsKey(id)
                    || !snapshot.Cached.TryGetValue(id, out var item) || !Usable(item)).Order().ToArray();
                if (snapshot.Truncated || snapshot.GraphNodes.Count > bound)
                    Unavailable(rule, snapshot.Scope[0], $"Dependency component exceeds the {bound}-item bound.");
                if (missing.Length > 0)
                    Unavailable(rule, snapshot.Scope[0], "Dependency items/links are unverified or locally modified; refresh before assessment.", missing);
                var known = snapshot.RawLinks.Where(l => snapshot.Verified.ContainsKey(l.SourceId)).ToArray();
                var edges = known.Select(Normalize).OfType<PolicyDependencyEdge>()
                    .Distinct().OrderBy(e => e.From).ThenBy(e => e.To).ToArray();
                foreach (var edge in edges.Where(e => e.From != e.To
                    && snapshot.Verified.ContainsKey(e.From) && snapshot.Verified.ContainsKey(e.To)))
                {
                    var forward = known.Any(l => l.SourceId == edge.From && Names.Equals(l.LinkType, LinkTypes.Successor)
                        && l.TargetId == edge.To);
                    var reverse = known.Any(l => l.SourceId == edge.To && Names.Equals(l.LinkType, LinkTypes.Predecessor)
                        && l.TargetId == edge.From);
                    if (!forward || !reverse)
                        Add(rule, PolicyFindingCode.EvidenceUnavailable, edge.From,
                            "Cached reciprocal dependency observations conflict. Refresh both endpoints before assessment.",
                            new() { Edges = [edge], MissingIds = [edge.From, edge.To] });
                }
                IEnumerable<PolicyRepairStrategy> RemoveChoices(IEnumerable<PolicyDependencyEdge> candidates) =>
                    candidates.Select((edge, index) => new PolicyRepairStrategy
                    {
                        Id = $"remove-edge-{index + 1}", RemoveEdge = edge,
                        Description = $"Remove dependency #{edge.From} -> #{edge.To}; retain the other edges.",
                    });
                foreach (var group in known.GroupBy(l => l).Where(g => g.Count() > 1)
                    .OrderBy(g => g.Key.SourceId).ThenBy(g => g.Key.TargetId).ThenBy(g => g.Key.LinkType, Names))
                {
                    var edge = Normalize(group.Key)!;
                    Add(rule, PolicyFindingCode.DuplicateDependency, group.Key.SourceId,
                        "Duplicate physical dependency relations are cached; explicitly remove this dependency, including all occurrences.",
                        new() { Edges = [edge], Count = group.Count() }, RemoveChoices([edge]));
                }
                foreach (var edge in edges.Where(e => e.From == e.To))
                    Add(rule, PolicyFindingCode.SelfDependency, edge.From, $"#{edge.From} depends on itself.",
                        new() { Edges = [edge] }, RemoveChoices([edge]));
                foreach (var cycle in FindCycles(edges.Where(e => e.From != e.To).ToArray()))
                    Add(rule, PolicyFindingCode.DependencyCycle, cycle[0].From,
                        "A dependency cycle exists. Explicitly choose which witnessed edge to remove.",
                        new() { Edges = cycle }, RemoveChoices(cycle));
                continue;
            }
            foreach (var id in snapshot.Scope)
            {
                if (!snapshot.Cached.TryGetValue(id, out var item) || !Usable(item))
                {
                    Unavailable(rule, id, $"#{id} has no synchronized, unmodified cached observation.", id);
                    continue;
                }
                if (rule is ParentTypeRule parentRule)
                {
                    var parentId = snapshot.Parents.GetValueOrDefault(id, item.ParentId);
                    if (!Names.Equals(item.Type.Value, parentRule.ChildType) || parentId is null)
                        continue; // This rule constrains existing edges, not required parenthood.
                    if (!snapshot.Cached.TryGetValue(parentId.Value, out var parent) || !Usable(parent))
                        Unavailable(rule, id, "The current parent's type is unavailable.", parentId.Value);
                    else if (!parentRule.AllowedParentTypes.Contains(parent.Type.Value, Names))
                        Add(rule, PolicyFindingCode.ParentType, id, "The current parent has a disallowed type.",
                            new()
                            {
                                ItemType = item.Type.Value, ParentId = parent.Id, ParentType = parent.Type.Value,
                                AllowedValues = parentRule.AllowedParentTypes,
                            },
                            [
                                new() { Id = "detach-parent", Description = "Remove this parent edge; do not choose a replacement." },
                                new() { Id = "reparent", RequiresParentId = true, Description = "Choose an explicit allowed parent from the assessed scope." },
                            ]);
                }
                else if (rule is LabelApplicabilityRule labelRule)
                {
                    if (!item.Fields.ContainsKey(labelRule.Field))
                    {
                        Unavailable(rule, id, $"Field '{labelRule.Field}' is absent, not verified empty.", id);
                        continue;
                    }
                    var tags = DeclaredPolicyEvaluator.SplitTags(
                        snapshot.Field(id, labelRule.Field, item.Fields[labelRule.Field]));
                    var prefixed = tags.Where(t => t.StartsWith(labelRule.Prefix, StringComparison.OrdinalIgnoreCase))
                        .Order(Names).ThenBy(t => t, StringComparer.Ordinal).ToArray();
                    var evidence = new PolicyFindingEvidence
                    {
                        ItemType = item.Type.Value, Field = labelRule.Field, Labels = prefixed,
                        AllowedValues = labelRule.AllowedTypes,
                    };
                    var remove = new PolicyRepairStrategy
                    {
                        Id = "remove-labels", Description = $"Remove all '{labelRule.Prefix}' labels; retain unrelated labels.",
                    };
                    if (prefixed.Length > 0 && !labelRule.AllowedTypes.Contains(item.Type.Value, Names))
                        Add(rule, PolicyFindingCode.LabelType, id, "Labels are not applicable to the current type.", evidence, [remove]);
                    if (labelRule.Exclusive && prefixed.Length > 1)
                        Add(rule, PolicyFindingCode.ExclusiveLabels, id, "Multiple exclusive labels are present.", evidence,
                            new[] { remove }.Concat(prefixed.Distinct(Names).Select((label, index) => new PolicyRepairStrategy
                            {
                                Id = $"retain-label-{index + 1}", RetainLabel = label,
                                Description = $"Retain '{label}' and remove the other '{labelRule.Prefix}' labels.",
                            })));
                }
                else
                    Unavailable(rule, id, $"Unsupported rule kind '{rule.Kind}'.");
            }
        }
        return new PolicyAssessment
        {
            Scope = snapshot.Scope, EvidenceDigest = Fingerprint(policy, snapshot),
            DependencyScope = snapshot.GraphNodes.Order().ToArray(),
            Items = snapshot.Cached.Keys.Concat(snapshot.Scope).Concat(snapshot.GraphNodes).Distinct().Order()
                .Select(id => new PolicyItemEvidence(id, snapshot.Cached.GetValueOrDefault(id)?.Revision,
                    snapshot.Cached.GetValueOrDefault(id)?.LastSyncedAt,
                    snapshot.Verified.TryGetValue(id, out var stamp) ? stamp : null)).ToArray(),
            Findings = findings,
        };
    }

    private static IReadOnlyList<IReadOnlyList<PolicyDependencyEdge>> FindCycles(PolicyDependencyEdge[] edges)
    {
        var outgoing = edges.ToLookup(e => e.From);
        var visited = new HashSet<int>();
        var active = new Dictionary<int, int>();
        var path = new List<PolicyDependencyEdge>();
        var cycles = new List<IReadOnlyList<PolicyDependencyEdge>>();
        void Visit(int node)
        {
            visited.Add(node);
            active[node] = path.Count;
            foreach (var edge in outgoing[node])
            {
                if (active.TryGetValue(edge.To, out var start))
                    cycles.Add(path.Skip(start).Append(edge).ToArray());
                else if (!visited.Contains(edge.To))
                {
                    path.Add(edge);
                    Visit(edge.To);
                    path.RemoveAt(path.Count - 1);
                }
            }
            active.Remove(node);
        }
        foreach (var node in edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Order())
            if (!visited.Contains(node)) Visit(node);
        return cycles;
    }

    private static PolicyDependencyEdge? Normalize(WorkItemLink link) =>
        Names.Equals(link.LinkType, LinkTypes.Successor) ? new(link.SourceId, link.TargetId)
        : Names.Equals(link.LinkType, LinkTypes.Predecessor) ? new(link.TargetId, link.SourceId) : null;

    private static string Fingerprint(DeclaredPolicy policy, Snapshot snapshot)
    {
        var text = new StringBuilder();
        void Part(object? value)
        {
            var s = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();
            text.Append(s?.Length ?? -1).Append(':').Append(s).Append(';');
        }
        foreach (var rule in policy.Rules)
        {
            Part(rule.Id); Part(rule.Kind); Part(rule.Enforcement);
            switch (rule)
            {
                case ParentTypeRule p:
                    Part(p.ChildType); foreach (var t in p.AllowedParentTypes) Part(t); break;
                case LabelApplicabilityRule l:
                    Part(l.Field); Part(l.Prefix); Part(l.Exclusive); foreach (var t in l.AllowedTypes) Part(t); break;
                case DependencyIntegrityRule d: Part(d.MaxNodes); break;
            }
            Part("end-rule");
        }
        foreach (var id in snapshot.Scope) Part(id);
        Part("end-scope"); Part(snapshot.Truncated);
        foreach (var source in snapshot.IncomingSources.Order()) Part(source);
        Part("end-incoming-sources");
        foreach (var (id, item) in snapshot.Cached.OrderBy(k => k.Key))
        {
            Part(id); Part(item.Revision); Part(item.LastSyncedAt?.ToString("o")); Part(item.IsDirty); Part(item.Type.Value);
            Part(snapshot.Parents.GetValueOrDefault(id, item.ParentId));
            foreach (var field in item.Fields.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                Part(field.Key); Part(snapshot.Field(id, field.Key, field.Value));
            }
            Part("end-item");
        }
        foreach (var id in snapshot.GraphNodes.Order()) { Part(id); Part(snapshot.Verified.GetValueOrDefault(id).ToString("o")); }
        foreach (var link in snapshot.RawLinks.OrderBy(l => l.SourceId).ThenBy(l => l.TargetId).ThenBy(l => l.LinkType, Names))
        { Part(link.SourceId); Part(link.TargetId); Part(link.LinkType); }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private sealed class Snapshot(IReadOnlyList<int> scope)
    {
        public IReadOnlyList<int> Scope { get; } = scope;
        public Dictionary<int, WorkItem> Cached { get; } = [];
        public Dictionary<int, int?> Parents { get; } = [];
        public Dictionary<int, Dictionary<string, string?>> Fields { get; } = [];
        public string? Field(int id, string field, string? fallback) =>
            Fields.TryGetValue(id, out var fields) ? fields.GetValueOrDefault(field, fallback) : fallback;
        public List<WorkItemLink> RawLinks { get; } = [];
        public Dictionary<int, DateTimeOffset> Verified { get; } = [];
        public HashSet<int> GraphNodes { get; } = [];
        public HashSet<int> IncomingSources { get; } = [];
        public bool Truncated { get; set; }
        public Snapshot Copy()
        {
            var copy = new Snapshot(Scope) { Truncated = Truncated };
            foreach (var pair in Cached) copy.Cached.Add(pair.Key, pair.Value);
            copy.RawLinks.AddRange(RawLinks);
            foreach (var pair in Verified) copy.Verified.Add(pair.Key, pair.Value);
            copy.GraphNodes.UnionWith(GraphNodes);
            copy.IncomingSources.UnionWith(IncomingSources);
            return copy;
        }
    }
}
