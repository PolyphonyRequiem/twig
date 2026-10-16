using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;

namespace Twig.Commands;

internal sealed record BenchSyncCounts(int ItemCount, int MemberCount, int RelationshipCount, int ProtectedCount);

/// <summary>Pull-only selector and relationship expansion shared by admitted CLI and browser operations.</summary>
internal sealed class BenchSyncOperation(CommandContext ctx, BenchEvaluator evaluator, IIterationCalendar calendar,
    IIterationService iterations, IAdoWorkItemService ado, IWorkItemRepository repository, ProtectedCacheWriter writer,
    IWorkItemLinkRepository links, IContextStore context)
{
    internal async Task<BenchSyncCounts> PullAsync(Bench bench, CancellationToken ct)
    {
        var queries = bench.Selectors.Where(s => s.Kind == SelectorKind.Query).ToArray();
        foreach (var selector in queries) _ = BenchQueryRule.Parse(selector);
        if (queries.Length > 0)
            await calendar.SaveAsync(await iterations.GetTeamIterationsAsync(ct), ct);
        var resolvedRules = new Dictionary<Twig.Domain.ValueObjects.BenchSelector, ResolvedBenchQueryRule>();
        foreach (var selector in queries)
            resolvedRules[selector] = await BenchQueryRule.Parse(selector).ResolveAsync(calendar, ct: ct);
        var membership = await evaluator.EvaluateResolvedAsync(bench, resolvedRules, ct);
        var ids = new HashSet<int>(membership.AllIds.Where(id => id > 0));
        foreach (var resolved in resolvedRules.Values)
        {
            var wiql = resolved.BuildWiql(ctx.Config.Project);
            if (wiql is null) continue;
            foreach (var id in await ado.QueryByWiqlAsync(wiql, ct))
                if (id > 0) ids.Add(id);
        }
        var memberIds = new HashSet<int>(membership.PinnedIds.Concat(membership.DirtyItemIds));
        var active = await context.GetActiveWorkItemIdAsync(ct);
        if (active is > 0) ids.Add(active.Value);

        var fetched = new Dictionary<int, WorkItem>();
        var protectedCount = 0;
        await FetchAsync(ids);
        var subtreeRoots = bench.Selectors.Where(s => s.Kind == SelectorKind.Subtree)
            .Select(s => s.AsWorkItemId()).Where(id => id > 0).Distinct().ToArray();
        await ExpandChildrenAsync(subtreeRoots, int.MaxValue, markAsMembers: true);
        if (active is > 0)
            await ExpandChildrenAsync([active.Value], Math.Max(0, ctx.Config.Display.TreeDepthDown));
        var relationshipRoots = fetched.Keys.ToArray();
        var parents = relationshipRoots.AsEnumerable();
        for (var depth = 0; depth < Math.Max(0, ctx.Config.Display.TreeDepthUp); depth++)
        {
            var next = parents.Select(id => fetched[id].ParentId).Where(id => id is > 0)
                .Select(id => id!.Value).Distinct().ToArray();
            if (next.Length == 0) break;
            await FetchAsync(next);
            parents = next;
        }
        if (active is > 0 && ctx.Config.Display.TreeDepthSideways > 0
            && fetched.TryGetValue(active.Value, out var activeItem) && activeItem.ParentId is > 0)
        {
            await FetchAsync([activeItem.ParentId.Value]);
            await ExpandChildrenAsync([activeItem.ParentId.Value], ctx.Config.Display.TreeDepthSideways);
        }
        foreach (var (id, remoteItem) in fetched)
        {
            var visible = await repository.GetByIdAsync(id, ct) ?? remoteItem;
            if (resolvedRules.Values.Any(rule => rule.Matches(visible))) memberIds.Add(id);
        }
        var memberCount = fetched.Keys.Count(memberIds.Contains);
        return new(fetched.Count, memberCount, fetched.Count - memberCount, protectedCount);

        async Task FetchAsync(IEnumerable<int> requested)
        {
            var missing = requested.Where(id => id > 0 && !fetched.ContainsKey(id)).Distinct().ToArray();
            if (missing.Length == 0) return;
            var (items, edges) = await ado.FetchBatchWithLinksAsync(missing, ct);
            var requestedIds = missing.ToHashSet();
            if (items.Any(item => !requestedIds.Contains(item.Id)) || items.Select(item => item.Id).Distinct().Count() != missing.Length)
                throw new InvalidOperationException("Bench sync received an incomplete or out-of-scope item batch.");
            protectedCount += (await writer.SaveBatchProtectedAsync(items, ct)).Count;
            var bySource = items.ToDictionary(item => item.Id,
                _ => (IReadOnlyList<Twig.Domain.ValueObjects.WorkItemLink>)Array.Empty<Twig.Domain.ValueObjects.WorkItemLink>());
            foreach (var group in edges.Where(edge => requestedIds.Contains(edge.SourceId)).GroupBy(edge => edge.SourceId))
                bySource[group.Key] = group.ToArray();
            await links.SaveLinksForSourcesAsync(bySource, ct);
            foreach (var item in items) fetched.Add(item.Id, item);
        }

        async Task ExpandChildrenAsync(IEnumerable<int> roots, int depthLimit, bool markAsMembers = false)
        {
            var visited = new HashSet<int>();
            var frontier = roots.Distinct().ToArray();
            for (var depth = 0; depth < depthLimit && frontier.Length > 0; depth++)
            {
                var children = new HashSet<int>();
                foreach (var id in frontier)
                {
                    if (!visited.Add(id)) continue;
                    foreach (var child in await ado.FetchChildrenAsync(id, ct))
                        if (child.Id > 0 && !visited.Contains(child.Id)) children.Add(child.Id);
                }
                if (markAsMembers) memberIds.UnionWith(children);
                await FetchAsync(children);
                frontier = children.ToArray();
            }
        }
    }
}
