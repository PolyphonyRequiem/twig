using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Works out which items are on a Bench, by evaluating its selectors against the LOCAL CACHE
/// (docs/specs/bench.spec.md, Solution).
/// <para>
/// 🔴 Selectors are never evaluated server-side. The obvious implementation — a Bench as a set of
/// ADO queries — is fatally wrong: ADO has never heard of a seed, cannot see an unpushed edit,
/// and reads are cache-only by ruling 0004 §3. A query selector still CARRIES an ADO query, but
/// that query is a refresh rule describing how matching items reach the cache; it is not what
/// runs when somebody looks at their Bench.
/// </para>
/// <para>
/// 🔴 Evaluation is order-free by construction. Every selector is evaluated independently against
/// the same cache state and the results are unioned, so two Benches holding the same selectors
/// produce identical output regardless of the order they were added in, and an item matched by
/// two selectors appears once. An implementation that folded selectors in sequence would pass
/// every other test while making construction order silently observable.
/// </para>
/// </summary>
public sealed class BenchEvaluator
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationCalendar _iterationCalendar;

    public BenchEvaluator(IWorkItemRepository workItemRepo, IIterationCalendar iterationCalendar)
    {
        _workItemRepo = workItemRepo;
        _iterationCalendar = iterationCalendar;
    }

    /// <summary>
    /// Evaluates every selector on the Bench and returns what each kind matched, grouped by kind.
    /// <para>
    /// The grouping is a PRESENTATION concern, not a membership one. Membership is the flat union
    /// (<see cref="BenchMembership.AllIds"/>); the groups exist because the read model displays
    /// hand pins differently from query results, and dropping that distinction would change what
    /// the person sees.
    /// </para>
    /// </summary>
    /// <param name="bench">The Bench to evaluate.</param>
    /// <param name="iterationOverride">
    /// When supplied, the sprint rule resolves to these iterations instead of asking the calendar.
    /// Callers that already know which iterations they mean pass them so no lookup happens.
    /// </param>
    public async Task<BenchMembership> EvaluateAsync(
        Bench bench,
        IReadOnlyList<IterationPath>? iterationOverride = null,
        CancellationToken ct = default)
    {
        var queryMatches = new List<WorkItem>();
        var queryMatchIds = new HashSet<int>();
        var pinnedIds = new List<int>();
        var pinnedIdSet = new HashSet<int>();
        var iterations = new List<IterationPath>();

        foreach (var selector in bench.Selectors)
        {
            switch (selector.Kind)
            {
                case SelectorKind.Query:
                {
                    var (items, resolvedIterations) =
                        await EvaluateQueryAsync(selector, iterationOverride, ct);

                    foreach (var path in resolvedIterations)
                    {
                        if (!iterations.Any(p => string.Equals(p.Value, path.Value, StringComparison.OrdinalIgnoreCase)))
                            iterations.Add(path);
                    }

                    // Deduplicated on the way in, so two query selectors matching the same item
                    // contribute one entry (spec: an item matched by two selectors appears once).
                    foreach (var item in items)
                    {
                        if (queryMatchIds.Add(item.Id))
                            queryMatches.Add(item);
                    }

                    break;
                }

                case SelectorKind.Item:
                {
                    var id = selector.AsWorkItemId();
                    if (pinnedIdSet.Add(id))
                        pinnedIds.Add(id);
                    break;
                }

                case SelectorKind.Subtree:
                {
                    // 🔴 The subtree is expanded HERE, at evaluation time, against the cache as it
                    // is now — never captured as a set of ids when the selector was added. That is
                    // what makes a subtree selector match a child created after it, and it is the
                    // distinction a naive implementation loses.
                    var rootId = selector.AsWorkItemId();
                    if (pinnedIdSet.Add(rootId))
                        pinnedIds.Add(rootId);

                    foreach (var descendantId in await GetDescendantIdsAsync(rootId, ct))
                    {
                        if (pinnedIdSet.Add(descendantId))
                            pinnedIds.Add(descendantId);
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException(
                        $"Unknown selector kind '{selector.Kind}' on Bench '{bench.Name}'.");
            }
        }

        return new BenchMembership
        {
            QueryMatches = queryMatches,
            PinnedIds = pinnedIds,
            IterationPaths = iterations,
        };
    }

    private async Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<IterationPath> Iterations)>
        EvaluateQueryAsync(
            BenchSelector selector,
            IReadOnlyList<IterationPath>? iterationOverride,
            CancellationToken ct)
    {
        var rule = selector.QueryRule;

        if (!string.Equals(rule, BenchSelector.CurrentSprintRule, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Unknown query rule '{rule}'. A rule is one named kind plus its settings; " +
                "a new rule is added beside this one rather than expressed within it.");

        var assignedTo = selector.QueryAssignedTo;

        // The sprint rule is "the iteration whose date range covers now". Which iteration that is
        // comes from the locally cached calendar and the local clock — never a network call, so a
        // Bench evaluates and displays with the ADO endpoint unreachable.
        var iterations = iterationOverride
            ?? await _iterationCalendar.GetCurrentIterationsAsync(ct);

        if (iterations.Count == 0)
            return ([], iterations);

        var items = await _workItemRepo.GetByIterationsAsync(iterations, ct);

        if (assignedTo is not null && items.Count > 0)
        {
            items = items
                .Where(w => string.Equals(w.AssignedTo, assignedTo, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return (items, iterations);
    }

    /// <summary>
    /// Walks the cached hierarchy under <paramref name="rootId"/>. Breadth-first with a visited
    /// set, so a cycle in cached parent links cannot spin forever.
    /// </summary>
    private async Task<IReadOnlyList<int>> GetDescendantIdsAsync(int rootId, CancellationToken ct)
    {
        var found = new List<int>();
        var visited = new HashSet<int> { rootId };
        var queue = new Queue<int>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var children = await _workItemRepo.GetChildrenAsync(current, ct);

            foreach (var child in children)
            {
                if (!visited.Add(child.Id))
                    continue;

                found.Add(child.Id);
                queue.Enqueue(child.Id);
            }
        }

        return found;
    }
}
