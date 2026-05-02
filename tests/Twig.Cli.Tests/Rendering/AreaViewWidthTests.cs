using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that area view rendering
/// (<see cref="SpectreRenderer.RenderAreaViewAsync"/> and <see cref="SpectreRenderer.FormatAreaNode"/>)
/// behaves correctly at narrow (60-char) and standard (80-char) terminal widths.
/// Covers hierarchy tree truncation, flat table truncation, depth budget, dirty markers,
/// out-of-area parent dimming, and width comparison.
/// </summary>
public sealed class AreaViewWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string MediumTitle = "Update user authentication flow for SSO";

    // ── Narrow (60) — hierarchy rendering ───────────────────────────

    [Fact]
    public async Task Hierarchy_NarrowWidth_RendersWithoutCrash()
    {
        var output = await RenderHierarchy(60, CreateSingleItemHierarchy(LongTitle));

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("Area View");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_ShortTitleVisible()
    {
        var output = await RenderHierarchy(60, CreateSingleItemHierarchy(ShortTitle));

        output.ShouldContain(ShortTitle);
        output.ShouldContain("#3");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_LongTitleTruncated()
    {
        var output = await RenderHierarchy(60, CreateSingleItemHierarchy(LongTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
        output.ShouldContain("#3");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_OutOfAreaParentPresent()
    {
        var output = await RenderHierarchy(60, CreateParentChildHierarchy("Parent Epic", ShortTitle));

        output.ShouldContain("Parent Epic");
        output.ShouldContain(ShortTitle);
        // Out-of-area parent should NOT show its ID
        output.ShouldNotContain("#1");
        // In-area child should show its ID
        output.ShouldContain("#3");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_LongOutOfAreaParentTruncated()
    {
        var output = await RenderHierarchy(60, CreateParentChildHierarchy(LongTitle, ShortTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_MultipleItems_AllIdsVisible()
    {
        var task1 = new WorkItemBuilder(10, "Alpha Task").AsTask().InState("Active").Build();
        var task2 = new WorkItemBuilder(11, "Beta Task").AsTask().InState("New").Build();
        var task3 = new WorkItemBuilder(12, "Gamma Task").AsTask().InState("Closed").Build();

        var inAreaItems = new[] { task1, task2, task3 };
        var parentLookup = inAreaItems.ToDictionary(i => i.Id);
        var hierarchy = new SprintHierarchyBuilder().Build(
            inAreaItems.ToList(),
            parentLookup,
            ceilingTypeNames: new List<string> { "Epic" });

        var filters = new List<AreaPathFilter> { new("Project\\Team A", true) };
        var view = AreaView.Build(inAreaItems, filters, hierarchy, matchCount: 3);
        var output = await RenderAreaView(60, view);

        output.ShouldContain("10");
        output.ShouldContain("11");
        output.ShouldContain("12");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_FiltersDisplayed()
    {
        var task = new WorkItemBuilder(3, ShortTitle).AsTask().InState("Active").Build();
        var filters = new List<AreaPathFilter>
        {
            new("Project\\Team A", IncludeChildren: true),
            new("Project\\Team B", IncludeChildren: false),
        };
        var view = AreaView.Build(new[] { task }, filters, matchCount: 1);
        var output = await RenderAreaView(60, view);

        output.ShouldContain("Filters (2)");
        output.ShouldContain("Team A");
        output.ShouldContain("under");
        output.ShouldContain("Team B");
        output.ShouldContain("exact");
    }

    [Fact]
    public async Task Hierarchy_NarrowWidth_EmptyView_ShowsMessage()
    {
        var filters = new List<AreaPathFilter> { new("Project\\Team A", true) };
        var view = AreaView.Build(Array.Empty<WorkItem>(), filters, matchCount: 0);
        var output = await RenderAreaView(60, view);

        output.ShouldContain("Items (0)");
        output.ShouldContain("No items match");
    }

    // ── Narrow (60) — flat table rendering ──────────────────────────

    [Fact]
    public async Task FlatTable_NarrowWidth_RendersWithoutCrash()
    {
        var output = await RenderFlatAreaView(60, LongTitle);

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("Area View");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_ShortTitleVisible()
    {
        var output = await RenderFlatAreaView(60, ShortTitle);

        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_LongTitleTruncated()
    {
        var output = await RenderFlatAreaView(60, LongTitle);

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task FlatTable_NarrowWidth_MultipleItems_AllIdsVisible()
    {
        var items = new[]
        {
            new WorkItemBuilder(10, "Task Alpha").AsTask().InState("Active").Build(),
            new WorkItemBuilder(11, "Task Beta").AsTask().InState("New").Build(),
            new WorkItemBuilder(12, "Task Gamma").AsTask().InState("Closed").Build(),
        };

        var filters = new List<AreaPathFilter> { new("Project", true) };
        var view = AreaView.Build(items, filters, hierarchy: null, matchCount: 3);
        var output = await RenderAreaView(60, view);

        output.ShouldContain("10");
        output.ShouldContain("11");
        output.ShouldContain("12");
    }

    // ── Narrow (60) — FormatAreaNode unit tests ─────────────────────

    [Fact]
    public void FormatAreaNode_NarrowWidth_InAreaLongTitle_Truncated()
    {
        var renderer = CreateRenderer(60);
        var item = new WorkItemBuilder(42, LongTitle).AsIssue().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(60);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldNotContain(LongTitle);
        result.ShouldContain("…");
        result.ShouldContain("#42");
    }

    [Fact]
    public void FormatAreaNode_NarrowWidth_OutOfAreaLongTitle_Truncated()
    {
        var renderer = CreateRenderer(60);
        var item = new WorkItemBuilder(10, LongTitle).AsEpic().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: false);
        var budget = new WidthBudget(60);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldNotContain(LongTitle);
        result.ShouldContain("…");
        result.ShouldStartWith("[dim]");
    }

    [Fact]
    public void FormatAreaNode_NarrowWidth_ShortTitle_NotTruncated()
    {
        var renderer = CreateRenderer(60);
        var item = new WorkItemBuilder(42, ShortTitle).AsTask().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(60);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldContain(ShortTitle);
        result.ShouldNotContain("…");
    }

    [Fact]
    public void FormatAreaNode_NarrowWidth_DirtyInAreaItem_ShowsMarker()
    {
        var renderer = CreateRenderer(60);
        var item = new WorkItemBuilder(99, ShortTitle).AsTask().InState("Active").Dirty().Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(60);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldContain("✎");
        result.ShouldContain("#99");
    }

    [Fact]
    public void FormatAreaNode_NarrowWidth_DirtyOutOfArea_NoDirtyMarker()
    {
        var renderer = CreateRenderer(60);
        var item = new WorkItemBuilder(99, ShortTitle).AsEpic().InState("Active").Dirty().Build();
        var node = new SprintHierarchyNode(item, isSprintItem: false);
        var budget = new WidthBudget(60);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldNotContain("✎");
        result.ShouldStartWith("[dim]");
    }

    [Fact]
    public void FormatAreaNode_NarrowWidth_DeeperDepth_ReducesBudget()
    {
        var renderer = CreateRenderer(60);
        var title = new string('X', 40);
        var item = new WorkItemBuilder(42, title).AsTask().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(60);

        var resultDepth0 = renderer.FormatAreaNode(node, budget, depth: 0);
        var resultDepth5 = renderer.FormatAreaNode(node, budget, depth: 5);

        // At narrow width, deeper depth should truncate more aggressively
        resultDepth5.ShouldContain("…");
        resultDepth0.ShouldContain("#42");
        resultDepth5.ShouldContain("#42");
    }

    // ── Standard (80) — hierarchy rendering ─────────────────────────

    [Fact]
    public async Task Hierarchy_StandardWidth_RendersWithoutCrash()
    {
        var output = await RenderHierarchy(80, CreateSingleItemHierarchy(LongTitle));

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("Area View");
    }

    [Fact]
    public async Task Hierarchy_StandardWidth_ShortTitleFullyVisible()
    {
        var output = await RenderHierarchy(80, CreateSingleItemHierarchy(ShortTitle));

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Hierarchy_StandardWidth_LongTitleTruncated()
    {
        var output = await RenderHierarchy(80, CreateSingleItemHierarchy(LongTitle));

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
        output.ShouldContain("#3");
    }

    [Fact]
    public async Task Hierarchy_StandardWidth_MediumTitleFits()
    {
        var output = await RenderHierarchy(80, CreateSingleItemHierarchy(MediumTitle));

        output.ShouldContain(MediumTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task Hierarchy_StandardWidth_OutOfAreaParentPresent()
    {
        var output = await RenderHierarchy(80, CreateParentChildHierarchy("Parent Epic", ShortTitle));

        output.ShouldContain("Parent Epic");
        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("#1");
        output.ShouldContain("#3");
    }

    // ── Standard (80) — flat table rendering ────────────────────────

    [Fact]
    public async Task FlatTable_StandardWidth_LongTitleTruncated()
    {
        var output = await RenderFlatAreaView(80, LongTitle);

        output.ShouldNotContain(LongTitle);
        output.ShouldContain("…");
    }

    [Fact]
    public async Task FlatTable_StandardWidth_MediumTitleFits()
    {
        var output = await RenderFlatAreaView(80, MediumTitle);

        output.ShouldContain(MediumTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task FlatTable_StandardWidth_ShortTitleFullyVisible()
    {
        var output = await RenderFlatAreaView(80, ShortTitle);

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    // ── Standard (80) — FormatAreaNode unit tests ───────────────────

    [Fact]
    public void FormatAreaNode_StandardWidth_MediumTitle_NotTruncated()
    {
        var renderer = CreateRenderer(80);
        var item = new WorkItemBuilder(42, MediumTitle).AsTask().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(80);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldContain(MediumTitle);
        result.ShouldNotContain("…");
    }

    [Fact]
    public void FormatAreaNode_StandardWidth_LongTitle_Truncated()
    {
        var renderer = CreateRenderer(80);
        var item = new WorkItemBuilder(42, LongTitle).AsIssue().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(80);

        var result = renderer.FormatAreaNode(node, budget, depth: 0);

        result.ShouldNotContain(LongTitle);
        result.ShouldContain("…");
    }

    [Fact]
    public void FormatAreaNode_StandardWidth_DeeperDepth_ReducesBudget()
    {
        var renderer = CreateRenderer(80);
        var title = new string('X', 50);
        var item = new WorkItemBuilder(42, title).AsTask().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);
        var budget = new WidthBudget(80);

        var resultDepth0 = renderer.FormatAreaNode(node, budget, depth: 0);
        var resultDepth5 = renderer.FormatAreaNode(node, budget, depth: 5);

        resultDepth0.ShouldNotContain("…");
        resultDepth5.ShouldContain("…");
    }

    // ── Width comparison ────────────────────────────────────────────

    [Fact]
    public async Task Hierarchy_NarrowVsStandard_BothContainId()
    {
        var hierarchyView = CreateSingleItemHierarchy(LongTitle);

        var narrow = await RenderHierarchy(60, hierarchyView);
        var standard = await RenderHierarchy(80, hierarchyView);

        narrow.ShouldContain("#3");
        standard.ShouldContain("#3");
    }

    [Fact]
    public void FormatAreaNode_NarrowTruncatesMore_ThanStandard()
    {
        var title = new string('A', 60);
        var item = new WorkItemBuilder(42, title).AsIssue().InState("Active").Build();
        var node = new SprintHierarchyNode(item, isSprintItem: true);

        var narrowRenderer = CreateRenderer(60);
        var narrowLabel = narrowRenderer.FormatAreaNode(node, new WidthBudget(60), depth: 0);

        var stdRenderer = CreateRenderer(80);
        var stdLabel = stdRenderer.FormatAreaNode(node, new WidthBudget(80), depth: 0);

        narrowLabel.ShouldContain("…");
        stdLabel.ShouldContain("…");
        narrowLabel.Length.ShouldBeLessThan(stdLabel.Length);
    }

    [Fact]
    public async Task FlatTable_NarrowVsStandard_NarrowTruncatesMore()
    {
        var title = new string('Z', 60);
        var narrowOutput = await RenderFlatAreaView(60, title);
        var stdOutput = await RenderFlatAreaView(80, title);

        narrowOutput.ShouldContain("…");
        stdOutput.ShouldContain("…");
        narrowOutput.Length.ShouldBeLessThan(stdOutput.Length);
    }

    [Fact]
    public async Task FlatTable_WideWidth_LongTitleFits()
    {
        var output = await RenderFlatAreaView(200, LongTitle);

        output.ShouldContain(LongTitle);
        output.ShouldNotContain("…");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static SpectreRenderer CreateRenderer(int width)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var console = new TestConsole { Profile = { Width = width } };
        return new SpectreRenderer(console, theme);
    }

    private static async Task<string> RenderAreaView(int width, AreaView view)
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var console = new TestConsole { Profile = { Width = width } };
        var renderer = new SpectreRenderer(console, theme);

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderHierarchy(int width, AreaView view)
    {
        return await RenderAreaView(width, view);
    }

    private static async Task<string> RenderFlatAreaView(int width, string title)
    {
        var item = new WorkItemBuilder(1, title).AsTask().InState("Active").Build();
        var filters = new List<AreaPathFilter> { new("Project", true) };
        var view = AreaView.Build(new[] { item }, filters, hierarchy: null, matchCount: 1);
        return await RenderAreaView(width, view);
    }

    /// <summary>
    /// Creates an AreaView with a single in-area item wrapped in hierarchy.
    /// </summary>
    private static AreaView CreateSingleItemHierarchy(string title)
    {
        var epic = new WorkItemBuilder(1, "Context Epic").AsEpic().InState("Active").Build();
        var task = new WorkItemBuilder(3, title).AsTask().InState("Active").WithParent(1).Build();

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1] = epic,
            [3] = task,
        };
        var hierarchy = new SprintHierarchyBuilder().Build(
            new List<WorkItem> { task },
            parentLookup,
            ceilingTypeNames: new List<string> { "Epic" });

        var filters = new List<AreaPathFilter> { new("Project\\Team A", true) };
        return AreaView.Build(new[] { task }, filters, hierarchy, matchCount: 1);
    }

    /// <summary>
    /// Creates an AreaView with an out-of-area parent and an in-area child.
    /// </summary>
    private static AreaView CreateParentChildHierarchy(string parentTitle, string childTitle)
    {
        var epic = new WorkItemBuilder(1, parentTitle).AsEpic().InState("Active").Build();
        var task = new WorkItemBuilder(3, childTitle).AsTask().InState("Active").WithParent(1).Build();

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1] = epic,
            [3] = task,
        };
        var hierarchy = new SprintHierarchyBuilder().Build(
            new List<WorkItem> { task },
            parentLookup,
            ceilingTypeNames: new List<string> { "Epic" });

        var filters = new List<AreaPathFilter> { new("Project\\Team A", true) };
        return AreaView.Build(new[] { task }, filters, hierarchy, matchCount: 1);
    }
}
