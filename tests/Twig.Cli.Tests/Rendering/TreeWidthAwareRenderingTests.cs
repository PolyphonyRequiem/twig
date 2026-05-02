using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests that FormatFocusedNode, FormatParentNode, and BuildSpectreTreeAsync/BuildTreeViewAsync
/// correctly truncate titles at narrow widths and preserve them at wide widths.
/// </summary>
public sealed class TreeWidthAwareRenderingTests
{
    private const string ShortTitle = "Fix bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";

    private static WorkItem CreateItem(int id, string title, WorkItemType? type = null, int? parentId = null) =>
        new()
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = "Active",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

    private static SpectreRenderer CreateRenderer(int width)
    {
        var testConsole = new TestConsole { Profile = { Width = width } };
        return new SpectreRenderer(testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── FormatFocusedNode ──────────────────────────────────────────────

    [Fact]
    public void FormatFocusedNode_WithBudget_TruncatesLongTitle()
    {
        var renderer = CreateRenderer(80);
        var budget = new WidthBudget(60);
        var item = CreateItem(42, LongTitle);

        var label = renderer.FormatFocusedNode(item, activeId: null, budget: budget, depth: 0);

        label.ShouldContain("…");
        label.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public void FormatFocusedNode_WithBudget_PreservesShortTitle()
    {
        var renderer = CreateRenderer(120);
        var budget = new WidthBudget(120);
        var item = CreateItem(42, ShortTitle);

        var label = renderer.FormatFocusedNode(item, activeId: null, budget: budget, depth: 0);

        label.ShouldContain(ShortTitle);
        label.ShouldNotContain("…");
    }

    [Fact]
    public void FormatFocusedNode_WithoutBudget_PreservesFullTitle()
    {
        var renderer = CreateRenderer(120);
        var item = CreateItem(42, LongTitle);

        var label = renderer.FormatFocusedNode(item, activeId: null);

        label.ShouldContain("exponential backoff");
        label.ShouldNotContain("…");
    }

    [Fact]
    public void FormatFocusedNode_ActiveMarkerStillApplied()
    {
        var renderer = CreateRenderer(80);
        var budget = new WidthBudget(60);
        var item = CreateItem(42, LongTitle);

        var label = renderer.FormatFocusedNode(item, activeId: 42, budget: budget, depth: 0);

        label.ShouldContain("●");
        label.ShouldContain("…");
    }

    [Fact]
    public void FormatFocusedNode_DeeperDepth_TruncatesMoreAggressively()
    {
        var renderer = CreateRenderer(80);
        var budget = new WidthBudget(80);
        var item = CreateItem(42, LongTitle);

        var labelAtZero = renderer.FormatFocusedNode(item, activeId: null, budget: budget, depth: 0);
        var labelAtFive = renderer.FormatFocusedNode(item, activeId: null, budget: budget, depth: 5);

        // Both truncated, but deeper depth should be shorter
        labelAtZero.ShouldContain("…");
        labelAtFive.ShouldContain("…");
        // Extract visible title length — the escaped title at depth 5 should be shorter
        var escapedAtZero = Markup.Remove(labelAtZero);
        var escapedAtFive = Markup.Remove(labelAtFive);
        escapedAtFive.Length.ShouldBeLessThan(escapedAtZero.Length);
    }

    // ── FormatParentNode ───────────────────────────────────────────────

    [Fact]
    public void FormatParentNode_WithBudget_TruncatesLongTitle()
    {
        var renderer = CreateRenderer(80);
        var budget = new WidthBudget(60);
        var item = CreateItem(1, LongTitle, WorkItemType.Epic);

        var label = renderer.FormatParentNode(item, aboveWorkingLevel: false, budget: budget, depth: 0);

        label.ShouldContain("…");
        label.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public void FormatParentNode_WithBudget_PreservesShortTitle()
    {
        var renderer = CreateRenderer(120);
        var budget = new WidthBudget(120);
        var item = CreateItem(1, ShortTitle, WorkItemType.Epic);

        var label = renderer.FormatParentNode(item, aboveWorkingLevel: false, budget: budget, depth: 0);

        label.ShouldContain(ShortTitle);
        label.ShouldNotContain("…");
    }

    [Fact]
    public void FormatParentNode_AboveWorkingLevel_WithBudget_StillTruncates()
    {
        var renderer = CreateRenderer(80);
        var budget = new WidthBudget(60);
        var item = CreateItem(1, LongTitle, WorkItemType.Epic);

        var label = renderer.FormatParentNode(item, aboveWorkingLevel: true, budget: budget, depth: 0);

        label.ShouldStartWith("[dim]");
        label.ShouldContain("…");
    }

    [Fact]
    public void FormatParentNode_WithoutBudget_PreservesFullTitle()
    {
        var renderer = CreateRenderer(120);
        var item = CreateItem(1, LongTitle, WorkItemType.Epic);

        var label = renderer.FormatParentNode(item, aboveWorkingLevel: false);

        label.ShouldContain("exponential backoff");
        label.ShouldNotContain("…");
    }

    // ── BuildSpectreTreeAsync ──────────────────────────────────────────

    [Fact]
    public async Task BuildSpectreTreeAsync_NarrowWidth_TruncatesParentAndFocus()
    {
        var renderer = CreateRenderer(60);
        var budget = new WidthBudget(60);

        var epic = CreateItem(1, LongTitle, WorkItemType.Epic);
        var task = CreateItem(2, LongTitle, WorkItemType.Task, parentId: 1);
        var parentChain = new List<WorkItem> { epic };

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, parentChain, activeId: 2, getSiblingCount: null, budget: budget);

        var output = RenderToString(tree, 60);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_WideWidth_PreservesFullTitles()
    {
        var renderer = CreateRenderer(200);
        var budget = new WidthBudget(200);

        var epic = CreateItem(1, ShortTitle, WorkItemType.Epic);
        var task = CreateItem(2, ShortTitle, WorkItemType.Task, parentId: 1);
        var parentChain = new List<WorkItem> { epic };

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, parentChain, activeId: 2, getSiblingCount: null, budget: budget);

        var output = RenderToString(tree, 200);
        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_NoBudget_BackwardsCompatible()
    {
        var renderer = CreateRenderer(120);

        var epic = CreateItem(1, LongTitle, WorkItemType.Epic);
        var task = CreateItem(2, LongTitle, WorkItemType.Task, parentId: 1);
        var parentChain = new List<WorkItem> { epic };

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, parentChain, activeId: 2, getSiblingCount: null);

        var output = RenderToString(tree, 120);
        // Without budget, titles should not be truncated
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_NoParents_FocusedNodeTruncated()
    {
        var renderer = CreateRenderer(60);
        var budget = new WidthBudget(60);

        var task = CreateItem(1, LongTitle, WorkItemType.Task);

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, Array.Empty<WorkItem>(), activeId: 1, getSiblingCount: null, budget: budget);

        var output = RenderToString(tree, 60);
        output.ShouldContain("…");
    }

    // ── BuildTreeViewAsync ─────────────────────────────────────────────

    [Fact]
    public async Task BuildTreeViewAsync_NarrowWidth_TruncatesChildTitles()
    {
        var renderer = CreateRenderer(60);

        var focused = CreateItem(1, ShortTitle, WorkItemType.Issue, parentId: null);
        var child = CreateItem(2, LongTitle, WorkItemType.Task, parentId: 1);

        var result = await renderer.BuildTreeViewAsync(
            focused, Array.Empty<WorkItem>(), new[] { child },
            maxDepth: 1, activeId: null);

        var output = RenderToString(result, 60);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task BuildTreeViewAsync_WideWidth_PreservesChildTitles()
    {
        var renderer = CreateRenderer(200);

        var focused = CreateItem(1, ShortTitle, WorkItemType.Issue, parentId: null);
        var child = CreateItem(2, ShortTitle, WorkItemType.Task, parentId: 1);

        var result = await renderer.BuildTreeViewAsync(
            focused, Array.Empty<WorkItem>(), new[] { child },
            maxDepth: 1, activeId: null);

        var output = RenderToString(result, 200);
        output.ShouldNotContain("…");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string RenderToString(IRenderable renderable, int width)
    {
        var console = new TestConsole { Profile = { Width = width } };
        console.Write(renderable);
        return console.Output;
    }
}
