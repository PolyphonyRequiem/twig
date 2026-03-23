using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class TreeSiblingCountTests
{
    private const string Dim = "\x1b[2m";
    private const string Reset = "\x1b[0m";

    private readonly HumanOutputFormatter _formatter = new();

    // ── Known sibling count ─────────────────────────────────────────

    [Fact]
    public void FormatTree_WithSiblingCounts_ShowsDimmedIndicator()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: 99);
        var focus = CreateWorkItem(10, "Focus", parentId: 1);
        var counts = new Dictionary<int, int?> { [1] = 5, [10] = 3 };
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>(), counts);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain($"{Dim}...5{Reset}");
        result.ShouldContain($"{Dim}...3{Reset}");
    }

    // ── Root node (null parent) — no sibling count ─────────────────

    [Fact]
    public void FormatTree_RootNode_OmitsSiblingCount()
    {
        var root = CreateWorkItem(1, "Root");
        var counts = new Dictionary<int, int?> { [1] = null };
        var tree = WorkTree.Build(root, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(), counts);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldNotContain("...");
    }

    // ── Parent chain root node has no parent — omit sibling count ──

    [Fact]
    public void FormatTree_ParentChainRootHasNoParent_OmitsSiblingCount()
    {
        var root = CreateWorkItem(1, "Root"); // no parentId
        var focus = CreateWorkItem(10, "Focus", parentId: 1);
        var counts = new Dictionary<int, int?> { [1] = null, [10] = 2 };
        var tree = WorkTree.Build(focus, new[] { root }, Array.Empty<WorkItem>(), counts);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Root node should not show sibling count (null = root)
        // Focus should show sibling count
        result.ShouldContain($"{Dim}...2{Reset}");
        // Only one "..." line — the focused item's
        var dotCount = CountOccurrences(result, "...");
        dotCount.ShouldBe(1);
    }

    // ── No sibling data (null dict) — no indicator lines ───────────

    [Fact]
    public void FormatTree_NullSiblingCounts_NoIndicators()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: 99);
        var focus = CreateWorkItem(10, "Focus", parentId: 1);
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldNotContain("...");
    }

    // ── Empty sibling counts dict — no indicator lines ─────────────

    [Fact]
    public void FormatTree_EmptySiblingCounts_NoIndicators()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: 99);
        var focus = CreateWorkItem(10, "Focus", parentId: 1);
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>(), new Dictionary<int, int?>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldNotContain("...");
    }

    // ── Multi-level parent chain — each with sibling count ─────────

    [Fact]
    public void FormatTree_DeepParentChain_ShowsSiblingCountForEach()
    {
        var grandparent = CreateWorkItem(1, "Epic", parentId: 99);
        var parent = CreateWorkItem(5, "Feature", parentId: 1);
        var focus = CreateWorkItem(10, "Story", parentId: 5);
        var counts = new Dictionary<int, int?> { [1] = 4, [5] = 7, [10] = 2 };
        var tree = WorkTree.Build(focus, new[] { grandparent, parent }, Array.Empty<WorkItem>(), counts);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain($"{Dim}...4{Reset}");
        result.ShouldContain($"{Dim}...7{Reset}");
        result.ShouldContain($"{Dim}...2{Reset}");
    }

    // ── Focused item as root (no parent) — no sibling count ────────

    [Fact]
    public void FormatTree_FocusedItemIsRoot_NoSiblingCount()
    {
        var focus = CreateWorkItem(10, "Root Focus");
        var child = CreateWorkItem(20, "Child", parentId: 10);
        var counts = new Dictionary<int, int?> { [10] = null };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child }, counts);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // "... and N more" shouldn't appear either — only 1 child and maxChildren=10
        result.ShouldNotContain($"{Dim}...");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static int CountOccurrences(string source, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
