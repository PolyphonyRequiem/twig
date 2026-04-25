using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests for SpectreRenderer tree-based workspace rendering: hierarchical layout,
/// working-level focus (dimmed ancestors, bold sprint items), depth limiting,
/// seed indicator preservation, and flat fallback.
/// </summary>
public sealed class WorkspaceTreeRenderTests
{
    private static readonly IReadOnlyDictionary<string, int> TypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["User Story"] = 2,
            ["Task"] = 3,
        };

    // ── Tree rendering basics ───────────────────────────────────────

    [Fact]
    public async Task TreeMode_RendersHierarchyFromTreeRoots()
    {
        var (console, renderer) = CreateTreeRenderer();

        var epic = new WorkItemBuilder(10, "My Epic").AsEpic().InState("Active").Build();
        var story = new WorkItemBuilder(20, "My Story").AsUserStory().InState("Active").Build();
        var task = new WorkItemBuilder(30, "My Task").AsTask().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story, isSprintItem: true, children: new[]
                {
                    BuildNode(task, isSprintItem: true)
                })
            })
        };

        var sections = BuildSectionsWithTree(new[] { story, task }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story, task }, sections: sections);

        output.ShouldContain("My Epic");
        output.ShouldContain("My Story");
        output.ShouldContain("My Task");
    }

    [Fact]
    public async Task TreeMode_SprintItemsRenderedBold()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story }, sections: sections);

        // Sprint items get bold formatting with #ID prefix
        output.ShouldContain("#20");
        output.ShouldContain("Sprint Story");
    }

    // ── Working-level focus ─────────────────────────────────────────

    [Fact]
    public async Task TreeMode_AncestorsAboveWorkingLevel_Dimmed()
    {
        var (console, renderer) = CreateTreeRenderer(workingLevel: "User Story");

        var epic = new WorkItemBuilder(10, "Context Epic").AsEpic().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Working Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story }, sections: sections);

        // Verify the epic label is dimmed (FormatWorkspaceTreeNodeLabel wraps in [dim])
        var label = renderer.FormatWorkspaceTreeNodeLabel(roots[0], null, 5);
        label.ShouldStartWith("[dim]");
        label.ShouldContain("Context Epic");
    }

    [Fact]
    public async Task TreeMode_ItemsAtWorkingLevel_NotDimmed()
    {
        var (console, renderer) = CreateTreeRenderer(workingLevel: "User Story");

        var story = new WorkItemBuilder(20, "Working Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };

        var label = renderer.FormatWorkspaceTreeNodeLabel(roots[0], null, 5);
        label.ShouldNotStartWith("[dim]");
        label.ShouldContain("Working Story");
    }

    [Fact]
    public async Task TreeMode_ItemsBelowWorkingLevel_NotDimmed()
    {
        var (console, renderer) = CreateTreeRenderer(workingLevel: "User Story");

        var task = new WorkItemBuilder(30, "Child Task").AsTask().InState("Active").Build();
        var roots = new[] { BuildNode(task, isSprintItem: true) };

        var label = renderer.FormatWorkspaceTreeNodeLabel(roots[0], null, 5);
        label.ShouldNotStartWith("[dim]");
        label.ShouldContain("Child Task");
    }

    // ── Depth limiting ──────────────────────────────────────────────

    [Fact]
    public async Task TreeMode_DepthDown_LimitsChildRendering()
    {
        var (console, renderer) = CreateTreeRenderer(depthDown: 1);

        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var task = new WorkItemBuilder(30, "Shallow Task").AsTask().InState("Active").Build();
        var subtask = new WorkItemBuilder(40, "Deep Subtask").AsTask().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(story, isSprintItem: true, children: new[]
            {
                BuildNode(task, isSprintItem: true, children: new[]
                {
                    BuildNode(subtask, isSprintItem: true)
                })
            })
        };

        var sections = BuildSectionsWithTree(new[] { story, task, subtask }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story, task, subtask }, sections: sections);

        // Depth 1 means only direct children of roots are shown
        output.ShouldContain("Story");
        output.ShouldContain("Shallow Task");
        // Subtask at depth 2 should be hidden, replaced with "more" indicator
        output.ShouldContain("more");
    }

    [Fact]
    public async Task TreeMode_DepthUp_PrunesAncestorsAboveLimit()
    {
        // TreeDepthUp=1 with working level "User Story" (level 2):
        // Epic (level 0) is 2 levels above → pruned; Feature (level 1) is 1 level above → kept
        var (console, renderer) = CreateTreeRenderer(workingLevel: "User Story", depthUp: 1);

        var epic = new WorkItemBuilder(10, "Pruned Epic").AsEpic().InState("Active").Build();
        var feature = new WorkItemBuilder(15, "Kept Feature").AsFeature().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Working Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(feature, isSprintItem: false, children: new[]
                {
                    BuildNode(story, isSprintItem: true)
                })
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story }, sections: sections);

        output.ShouldNotContain("Pruned Epic");
        output.ShouldContain("Kept Feature");
        output.ShouldContain("Working Story");
    }

    [Fact]
    public async Task TreeMode_DepthUp_ZeroPrunesAllAncestors()
    {
        // TreeDepthUp=0 means no ancestors above working level are shown
        var (console, renderer) = CreateTreeRenderer(workingLevel: "User Story", depthUp: 0);

        var epic = new WorkItemBuilder(10, "Pruned Epic").AsEpic().InState("Active").Build();
        var feature = new WorkItemBuilder(15, "Pruned Feature").AsFeature().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Only Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(feature, isSprintItem: false, children: new[]
                {
                    BuildNode(story, isSprintItem: true)
                })
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story }, sections: sections);

        output.ShouldNotContain("Pruned Epic");
        output.ShouldNotContain("Pruned Feature");
        output.ShouldContain("Only Story");
    }

    [Fact]
    public void PruneAncestors_NoTypeLevelMap_ReturnsOriginalRoots()
    {
        var (_, renderer) = CreateTreeRenderer(); // no working level set → TypeLevelMap null
        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };

        var result = renderer.PruneAncestorsAboveDepthUp(roots);
        result.ShouldBe(roots);
    }

    [Fact]
    public void PruneAncestors_VirtualGroupsPreserved()
    {
        var (_, renderer) = CreateTreeRenderer(workingLevel: "User Story", depthUp: 0);
        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[] { BuildNode(task, isSprintItem: true) });

        var result = renderer.PruneAncestorsAboveDepthUp(new[] { virtualRoot });
        result.Count.ShouldBe(1);
        result[0].IsVirtualGroup.ShouldBeTrue();
    }

    [Fact]
    public async Task TreeMode_DepthSidewaysZero_HidesTruncationIndicator()
    {
        // TreeDepthSideways=0 suppresses the "...N more" indicator
        var (console, renderer) = CreateTreeRenderer(depthDown: 1, depthSideways: 0);

        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var task = new WorkItemBuilder(30, "Task").AsTask().InState("Active").Build();
        var subtask = new WorkItemBuilder(40, "SubTask").AsTask().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(story, isSprintItem: true, children: new[]
            {
                BuildNode(task, isSprintItem: true, children: new[]
                {
                    BuildNode(subtask, isSprintItem: true)
                })
            })
        };

        var sections = BuildSectionsWithTree(new[] { story, task, subtask }, roots);
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story, task, subtask }, sections: sections);

        output.ShouldContain("Story");
        output.ShouldContain("Task");
        // With depthSideways=0, the "more" indicator is hidden even though depth limits truncate
        output.ShouldNotContain("more");
    }

    // ── Virtual group nodes ─────────────────────────────────────────

    [Fact]
    public async Task TreeMode_VirtualGroupNode_RenderedDimItalic()
    {
        var (console, renderer) = CreateTreeRenderer();

        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[]
        {
            BuildNode(task, isSprintItem: true)
        });

        var sections = BuildSectionsWithTree(new[] { task }, new[] { virtualRoot });
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { task }, sections: sections);

        output.ShouldContain("Unparented Tasks");
        output.ShouldContain("Orphan Task");
    }

    // ── Seed indicators preserved ───────────────────────────────────

    [Fact]
    public async Task TreeMode_Seeds_ShowSeedIndicator()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "My Seed").AsSeed().Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWorkspaceWithSeeds(console, renderer,
            sprintItems: new[] { story }, sections: sections, seeds: new[] { seed });

        output.ShouldContain("●");
        output.ShouldContain("My Seed");
    }

    [Fact]
    public async Task TreeMode_StaleSeed_ShowsStaleMarker()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-2, "Old Seed").AsSeed(daysOld: 30).Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWorkspaceWithSeeds(console, renderer,
            sprintItems: new[] { story }, sections: sections,
            seeds: new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
    }

    // ── Section headers ─────────────────────────────────────────────

    [Fact]
    public async Task TreeMode_SingleSection_NoSectionHeader()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { story }, sections: sections);

        output.ShouldNotContain("── Sprint");
    }

    [Fact]
    public async Task TreeMode_MultipleSections_ShowsSectionHeaders()
    {
        var (console, renderer) = CreateTreeRenderer();

        var sprintStory = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var areaTask = new WorkItemBuilder(30, "Area Task").AsTask().InState("Active").Build();

        var sprintRoots = new[] { BuildNode(sprintStory, isSprintItem: true) };
        var areaRoots = new[] { BuildNode(areaTask, isSprintItem: true) };

        var sections = new WorkspaceSections_TestHelper(
            new WorkspaceSection("Sprint", new[] { sprintStory }, sprintRoots),
            new WorkspaceSection("Area", new[] { areaTask }, areaRoots));

        var allItems = new[] { sprintStory, areaTask };
        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: allItems, sections: sections.Build());

        output.ShouldContain("Sprint");
        output.ShouldContain("Area");
        output.ShouldContain("Sprint Story");
        output.ShouldContain("Area Task");
    }

    // ── Active context highlighting ─────────────────────────────────

    [Fact]
    public async Task TreeMode_ActiveContextItem_ShowsMarker()
    {
        var (console, renderer) = CreateTreeRenderer();

        var activeStory = new WorkItemBuilder(20, "Active Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(activeStory, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { activeStory }, roots);

        var output = await RenderTreeWorkspaceWithContext(console, renderer,
            contextItem: activeStory,
            sprintItems: new[] { activeStory }, sections: sections);

        output.ShouldContain("►");
        output.ShouldContain("Active Story");
    }

    // ── Exclusion footer ────────────────────────────────────────────

    [Fact]
    public async Task TreeMode_ExcludedItems_ShowsFooter()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTreeAndExclusions(
            new[] { story }, roots, excludedIds: new[] { 42, 99 });

        var output = await RenderTreeWorkspaceWithSeeds(console, renderer,
            sprintItems: new[] { story }, sections: sections,
            seeds: Array.Empty<WorkItem>());

        output.ShouldContain("2 excluded");
        output.ShouldContain("#42");
        output.ShouldContain("#99");
    }

    // ── Flat fallback in tree mode ──────────────────────────────────

    [Fact]
    public async Task TreeMode_NullSections_FallsBackToFlatInContainer()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Flat Story").AsUserStory().InState("Active").Build();

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { story }, Sections: null),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        var output = console.Output;

        output.ShouldContain("Flat Story");
    }

    [Fact]
    public async Task TreeMode_SectionsWithoutTreeRoots_FallsBackToFlatInSection()
    {
        var (console, renderer) = CreateTreeRenderer();

        var story = new WorkItemBuilder(20, "Flat Section Story").AsUserStory().InState("Active").Build();
        var sections = WorkspaceSections.Build(new[] { story });

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { story }, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        var output = console.Output;

        output.ShouldContain("Flat Section Story");
    }

    // ── Progress footer in tree mode ────────────────────────────────

    [Fact]
    public async Task TreeMode_ProgressFooter_ShowsDoneCount()
    {
        var (console, renderer) = CreateTreeRenderer();

        var active = new WorkItemBuilder(20, "Active Story").AsUserStory().InState("Active").Build();
        var done = new WorkItemBuilder(21, "Done Story").AsUserStory().InState("Closed").Build();

        var roots = new[]
        {
            BuildNode(active, isSprintItem: true),
            BuildNode(done, isSprintItem: true)
        };
        var sections = BuildSectionsWithTree(new[] { active, done }, roots);

        var output = await RenderTreeWorkspace(console, renderer,
            sprintItems: new[] { active, done }, sections: sections);

        output.ShouldContain("done");
    }

    // ── Context-only nodes ──────────────────────────────────────────

    [Fact]
    public async Task TreeMode_ContextOnlyNode_TitleDimmed()
    {
        var (console, renderer) = CreateTreeRenderer(workingLevel: "Feature");

        // User Story at level 2 is below Feature (level 1), so NOT above working level.
        // But it's context-only (not a sprint item), so title should be dimmed.
        var story = new WorkItemBuilder(10, "Context Story").AsUserStory().InState("Active").Build();

        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: false), null, 5);

        // Context nodes at/below working level: type badge visible, title dimmed
        label.ShouldNotStartWith("[dim]");
        label.ShouldContain("[dim]Context Story[/]");
    }

    // ── Tracked items ───────────────────────────────────────────────

    [Fact]
    public async Task TreeMode_TrackedItem_ShowsPinnedMarker()
    {
        var (console, renderer) = CreateTreeRenderer();
        renderer.TrackedItemIds = new HashSet<int> { 20 };

        var story = new WorkItemBuilder(20, "Tracked Story").AsUserStory().InState("Active").Build();

        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5);

        label.ShouldContain("📌");
        label.ShouldContain("Tracked Story");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (TestConsole Console, SpectreRenderer Renderer) CreateTreeRenderer(
        string? workingLevel = null,
        int depthUp = 2,
        int depthDown = 10,
        int depthSideways = 1)
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        var theme = new SpectreTheme(new DisplayConfig());
        var renderer = new SpectreRenderer(console, theme)
        {
            UseTreeRendering = true,
            TreeDepthUp = depthUp,
            TreeDepthDown = depthDown,
            TreeDepthSideways = depthSideways,
        };

        if (workingLevel is not null)
        {
            renderer.TypeLevelMap = TypeLevelMap;
            renderer.WorkingLevelTypeName = workingLevel;
        }

        return (console, renderer);
    }

    private static SprintHierarchyNode BuildNode(
        WorkItem item, bool isSprintItem,
        SprintHierarchyNode[]? children = null)
    {
        var node = new SprintHierarchyNode(item, isSprintItem);
        if (children is not null)
        {
            foreach (var child in children)
                node.Children.Add(child);
        }
        return node;
    }

    private static SprintHierarchyNode BuildVirtualGroupNode(
        string label, SprintHierarchyNode[] children)
    {
        var node = new SprintHierarchyNode(label, backlogLevel: 0);
        foreach (var child in children)
            node.Children.Add(child);
        return node;
    }

    private static WorkspaceSections BuildSectionsWithTree(
        WorkItem[] items,
        SprintHierarchyNode[] treeRoots)
    {
        return WorkspaceSections.Build(items, treeRoots: treeRoots);
    }

    private static WorkspaceSections BuildSectionsWithTreeAndExclusions(
        WorkItem[] items,
        SprintHierarchyNode[] treeRoots,
        int[] excludedIds)
    {
        return WorkspaceSections.Build(items, treeRoots: treeRoots, excludedIds: excludedIds);
    }

    private static async Task<string> RenderTreeWorkspace(
        TestConsole console, SpectreRenderer renderer,
        WorkItem[] sprintItems, WorkspaceSections sections)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderTreeWorkspaceWithContext(
        TestConsole console, SpectreRenderer renderer,
        WorkItem contextItem, WorkItem[] sprintItems, WorkspaceSections sections)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(contextItem),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, sections),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderTreeWorkspaceWithSeeds(
        TestConsole console, SpectreRenderer renderer,
        WorkItem[] sprintItems, WorkspaceSections sections,
        WorkItem[] seeds, int staleDays = 14)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems, sections),
            new WorkspaceDataChunk.SeedsLoaded(seeds));

        await renderer.RenderWorkspaceAsync(chunks, staleDays, false, CancellationToken.None);
        return console.Output;
    }

    private static async IAsyncEnumerable<WorkspaceDataChunk> CreateChunksAsync(
        params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    /// <summary>
    /// Helper to build WorkspaceSections with multiple sections containing tree roots.
    /// </summary>
    private sealed class WorkspaceSections_TestHelper
    {
        private readonly WorkspaceSection[] _sections;

        public WorkspaceSections_TestHelper(params WorkspaceSection[] sections)
        {
            _sections = sections;
        }

        public WorkspaceSections Build()
        {
            // Build with sprint items from first section, area from second
            var sprint = _sections.Length > 0 ? _sections[0].Items : Array.Empty<WorkItem>();
            var area = _sections.Length > 1 ? _sections[1].Items : null;
            var result = WorkspaceSections.Build(sprint, areaItems: area);
            // Return custom sections via the Build method — but we need tree roots
            // So we use the overload with treeRoots on the sprint items
            // For multi-section with trees, we rebuild manually
            return WorkspaceSections.BuildWithTreeRoots(_sections);
        }
    }
}
