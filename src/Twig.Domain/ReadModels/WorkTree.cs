using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.ReadModels;

/// <summary>
/// Immutable composite read model for navigating a work item hierarchy.
/// Navigation methods return IDs, not mutated trees — the CLI layer builds
/// a new tree at the target ID.
/// </summary>
public sealed class WorkTree
{
    /// <summary>The focused (current) work item.</summary>
    public WorkItem FocusedItem { get; }

    /// <summary>Ordered parent chain from root ancestor → immediate parent.</summary>
    public IReadOnlyList<WorkItem> ParentChain { get; }

    /// <summary>Direct children of the focused item.</summary>
    public IReadOnlyList<WorkItem> Children { get; }

    /// <summary>
    /// Optional sibling counts keyed by work item ID.
    /// Value is <c>null</c> for root nodes (no parent to query); otherwise the count of children under that node's parent.
    /// </summary>
    public IReadOnlyDictionary<int, int?>? SiblingCounts { get; }

    /// <summary>
    /// Non-hierarchy links (Related, Predecessor, Successor) for the focused item.
    /// Empty when no links exist or links have not been fetched.
    /// </summary>
    public IReadOnlyList<WorkItemLink> FocusedItemLinks { get; }

    private WorkTree(WorkItem focusedItem, IReadOnlyList<WorkItem> parentChain, IReadOnlyList<WorkItem> children,
        IReadOnlyDictionary<int, int?>? siblingCounts, IReadOnlyList<WorkItemLink> focusedItemLinks)
    {
        FocusedItem = focusedItem;
        ParentChain = parentChain;
        Children = children;
        SiblingCounts = siblingCounts;
        FocusedItemLinks = focusedItemLinks;
    }

    /// <summary>
    /// Builds an immutable <see cref="WorkTree"/> from the focused item, its parent chain, and children.
    /// </summary>
    public static WorkTree Build(
        WorkItem focus,
        IReadOnlyList<WorkItem> parentChain,
        IReadOnlyList<WorkItem> children,
        IReadOnlyDictionary<int, int?>? siblingCounts = null,
        IReadOnlyList<WorkItemLink>? focusedItemLinks = null)
    {
        return new WorkTree(focus, parentChain, children, siblingCounts,
            focusedItemLinks ?? Array.Empty<WorkItemLink>());
    }

    /// <summary>
    /// Searches children by pattern, delegating to <see cref="PatternMatcher"/>.
    /// </summary>
    public MatchResult FindByPattern(string pattern)
    {
        var candidates = BuildCandidates();
        return PatternMatcher.Match(pattern, candidates);
    }

    /// <summary>
    /// Navigates to the parent. Returns the parent's ID, or null if at root.
    /// </summary>
    public int? MoveUp()
    {
        if (ParentChain.Count == 0)
            return null;

        return ParentChain[^1].Id;
    }

    /// <summary>
    /// Navigates to a child by ID or pattern.
    /// Returns the child's ID on single match, or an error on no/multiple matches.
    /// </summary>
    public Result<int> MoveDown(string idOrPattern)
    {
        var candidates = BuildCandidates();
        var result = PatternMatcher.Match(idOrPattern, candidates);

        return result switch
        {
            MatchResult.SingleMatch single => Result.Ok(single.Id),
            MatchResult.MultipleMatches multi => Result.Fail<int>(
                $"Ambiguous: {multi.Candidates.Count} children match '{idOrPattern}'."),
            MatchResult.NoMatch => Result.Fail<int>(
                $"No child matches '{idOrPattern}'."),
            _ => Result.Fail<int>("Unexpected match result."),
        };
    }

    private IReadOnlyList<(int Id, string Title)> BuildCandidates()
    {
        var candidates = new List<(int Id, string Title)>(Children.Count);
        foreach (var child in Children)
        {
            candidates.Add((child.Id, child.Title));
        }

        return candidates;
    }
}
