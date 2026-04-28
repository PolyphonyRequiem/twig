using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests for area-view rendering in SpectreRenderer. Verifies that:
/// - In-area items (IsSprintItem=true) render with full detail (ID, type, title, state)
/// - Out-of-area parent items (IsSprintItem=false) render with dim styling
/// - Empty area views show appropriate messaging
/// - Flat fallback works when no hierarchy is available
/// </summary>
public sealed class AreaViewRenderingTests
{
    // ── FormatAreaNode ─────────────────────────────────────────────

    [Fact]
    public void FormatAreaNode_InAreaItem_ShowsIdAndTitle()
    {
        var renderer = CreateRenderer();
        var item = CreateWorkItem(42, "My Feature", WorkItemType.Issue);
        var node = new SprintHierarchyNode(item, isSprintItem: true);

        var result = renderer.FormatAreaNode(node);

        result.ShouldContain("#42");
        result.ShouldContain("My Feature");
        result.ShouldNotStartWith("[dim]");
    }

    [Fact]
    public void FormatAreaNode_OutOfAreaParent_WrappedInDim()
    {
        var renderer = CreateRenderer();
        var item = CreateWorkItem(10, "Parent Epic", WorkItemType.Epic);
        var node = new SprintHierarchyNode(item, isSprintItem: false);

        var result = renderer.FormatAreaNode(node);

        result.ShouldStartWith("[dim]");
        result.ShouldEndWith("[/]");
        result.ShouldContain("Parent Epic");
        // Out-of-area parents should NOT show the ID
        result.ShouldNotContain("#10");
    }

    [Fact]
    public void FormatAreaNode_DirtyInAreaItem_ShowsDirtyMarker()
    {
        var renderer = CreateRenderer();
        var item = CreateWorkItem(99, "Dirty Item", WorkItemType.Task, isDirty: true);
        var node = new SprintHierarchyNode(item, isSprintItem: true);

        var result = renderer.FormatAreaNode(node);

        result.ShouldContain("✎");
    }

    [Fact]
    public void FormatAreaNode_DirtyOutOfAreaParent_NoDirtyMarker()
    {
        var renderer = CreateRenderer();
        var item = CreateWorkItem(99, "Dirty Parent", WorkItemType.Epic, isDirty: true);
        var node = new SprintHierarchyNode(item, isSprintItem: false);

        var result = renderer.FormatAreaNode(node);

        result.ShouldNotContain("✎");
    }

    // ── RenderAreaViewAsync — empty view ──────────────────────────

    [Fact]
    public async Task RenderAreaViewAsync_EmptyView_ShowsNoItemsMessage()
    {
        var (renderer, console) = CreateRendererWithConsole();
        var filters = new List<AreaPathFilter>
        {
            new("Project\\Team A", IncludeChildren: true),
        };
        var view = AreaView.Build(
            Array.Empty<WorkItem>(),
            filters,
            matchCount: 0);

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Area View");
        output.ShouldContain("Filters (1)");
        output.ShouldContain("Team A");
        output.ShouldContain("Items (0)");
        output.ShouldContain("No items match");
    }

    // ── RenderAreaViewAsync — flat fallback (no hierarchy) ────────

    [Fact]
    public async Task RenderAreaViewAsync_NoHierarchy_RendersFlatTable()
    {
        var (renderer, console) = CreateRendererWithConsole();
        var items = new List<WorkItem>
        {
            CreateWorkItem(1, "Task One", WorkItemType.Task),
            CreateWorkItem(2, "Task Two", WorkItemType.Task),
        };
        var filters = new List<AreaPathFilter> { new("Project", true) };
        var view = AreaView.Build(items, filters, hierarchy: null, matchCount: 2);

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Items (2)");
        output.ShouldContain("Task One");
        output.ShouldContain("Task Two");
    }

    // ── RenderAreaViewAsync — hierarchy with dimming ──────────────

    [Fact]
    public async Task RenderAreaViewAsync_WithHierarchy_InAreaItemShowsId()
    {
        var (renderer, console) = CreateRendererWithConsole();
        var view = BuildHierarchyView();

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);

        var output = console.Output;
        // In-area task should show its ID
        output.ShouldContain("#3");
        output.ShouldContain("In-Area Task");
    }

    [Fact]
    public async Task RenderAreaViewAsync_WithHierarchy_OutOfAreaParentOmitsId()
    {
        var (renderer, console) = CreateRendererWithConsole();
        var view = BuildHierarchyView();

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);

        var output = console.Output;
        // Out-of-area parent should be present but should NOT show its ID
        output.ShouldContain("Parent Epic");
        output.ShouldNotContain("#1");
    }

    // ── RenderAreaViewAsync — multiple filters ────────────────────

    [Fact]
    public async Task RenderAreaViewAsync_MultipleFilters_ShowsAllWithSemantics()
    {
        var (renderer, console) = CreateRendererWithConsole();
        var filters = new List<AreaPathFilter>
        {
            new("Project\\Team A", IncludeChildren: true),
            new("Project\\Team B", IncludeChildren: false),
        };
        var view = AreaView.Build(Array.Empty<WorkItem>(), filters, matchCount: 0);

        await renderer.RenderAreaViewAsync(view, CancellationToken.None);

        var output = console.Output;
        output.ShouldContain("Filters (2)");
        output.ShouldContain("Team A");
        output.ShouldContain("under");
        output.ShouldContain("Team B");
        output.ShouldContain("exact");
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static SpectreRenderer CreateRenderer()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        return new SpectreRenderer(testConsole, theme);
    }

    private static (SpectreRenderer Renderer, TestConsole Console) CreateRendererWithConsole()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        testConsole.Profile.Width = 120;
        return (new SpectreRenderer(testConsole, theme), testConsole);
    }

    /// <summary>
    /// Builds an AreaView with a hierarchy containing an out-of-area parent and an in-area child.
    /// Uses new SprintHierarchyBuilder().Build() with the in-area task as the sprint item and the epic as
    /// a parent-context node (out-of-area, IsSprintItem=false).
    /// </summary>
    private static AreaView BuildHierarchyView()
    {
        var outOfAreaEpic = CreateWorkItem(1, "Parent Epic", WorkItemType.Epic);
        var inAreaTask = CreateWorkItem(3, "In-Area Task", WorkItemType.Task, parentId: 1);

        // Build hierarchy: task is the "sprint item" (in-area), epic is parent context (out-of-area)
        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1] = outOfAreaEpic,
            [3] = inAreaTask,
        };
        var hierarchy = new SprintHierarchyBuilder().Build(
            new List<WorkItem> { inAreaTask },
            parentLookup,
            ceilingTypeNames: new List<string> { "Epic" });

        var filters = new List<AreaPathFilter> { new("Project\\Team A", true) };
        return AreaView.Build(new List<WorkItem> { inAreaTask }, filters, hierarchy, matchCount: 1);
    }

    private static WorkItem CreateWorkItem(int id, string title, WorkItemType? type = null, int? parentId = null, bool isDirty = false)
    {
        var item = new WorkItem
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = "Active",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        if (isDirty)
            item.SetDirty();
        return item;
    }
}
