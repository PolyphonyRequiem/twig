using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// What a Bench's selectors matched, at one moment, against the local cache.
/// <para>
/// 🔴 Membership is <see cref="AllIds"/> — one flat, order-free union. The per-kind groups exist
/// because the view DISPLAYS a hand pin differently from a query result; they are a presentation
/// split, not two memberships. An item matched by both appears in both groups and once in the
/// union, which is exactly the overlap case the spec requires.
/// </para>
/// </summary>
public sealed record BenchMembership
{
    /// <summary>Items matched by query selectors, in the cache's own order, deduplicated.</summary>
    public IReadOnlyList<WorkItem> QueryMatches { get; init; } = [];

    /// <summary>
    /// Ids matched by item and subtree selectors, deduplicated. Subtree selectors are already
    /// expanded against the cache as it is now.
    /// </summary>
    public IReadOnlyList<int> PinnedIds { get; init; } = [];

    /// <summary>The iterations the sprint rule resolved to, for callers that report scope.</summary>
    public IReadOnlyList<IterationPath> IterationPaths { get; init; } = [];

    /// <summary>
    /// Locally drafted items that have never been pushed (ADO #147).
    /// <para>
    /// 🔴 NOT selector-derived. ADO has never heard of a seed, so no rule on any Bench could
    /// match one; surfacing them is a property of what an evaluation RETURNS.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> SeedIds { get; init; } = [];

    /// <summary>
    /// Items carrying unpushed edits — what twig OWES ADO (ADO #147).
    /// <para>
    /// 🔴 Also not selector-derived, and for a different reason from the seeds: a Bench's
    /// selectors decide what is INTERESTING, and a display preference must not be able to
    /// conceal a debt. If switching Bench could hide a staged edit, twig would be using a view
    /// setting to hide work that is lost when the person forgets it.
    /// </para>
    /// </summary>
    public IReadOnlySet<int> DirtyItemIds { get; init; } = new HashSet<int>();

    /// <summary>
    /// What a Bench evaluation surfaces: the union of everything its selectors matched, PLUS the
    /// owed work that no selector can remove.
    /// <para>
    /// 🔴 The owed ids are unioned HERE rather than added as selectors at Bench creation. A
    /// selector can be removed by editing the Bench; this cannot. That difference is the whole
    /// of ADO #147 — an implementation that seeds every Bench with a "show my unpushed work"
    /// selector passes the same acceptance sentences while reproducing the defect.
    /// </para>
    /// </summary>
    public IReadOnlySet<int> AllIds
    {
        get
        {
            var set = new HashSet<int>();
            foreach (var item in QueryMatches) set.Add(item.Id);
            foreach (var id in PinnedIds) set.Add(id);
            foreach (var id in SeedIds) set.Add(id);
            foreach (var id in DirtyItemIds) set.Add(id);
            return set;
        }
    }

    /// <summary>
    /// Only what the SELECTORS matched. Exists so a test can assert the discriminating
    /// precondition — "this item really is matched by nothing on this Bench" — rather than
    /// trusting a fixture comment and degrading into a tautology.
    /// </summary>
    public IReadOnlySet<int> SelectedIds
    {
        get
        {
            var set = new HashSet<int>();
            foreach (var item in QueryMatches) set.Add(item.Id);
            foreach (var id in PinnedIds) set.Add(id);
            return set;
        }
    }
}
