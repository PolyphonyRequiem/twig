using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests verifying that workspace tree rendering
/// (<see cref="SpectreRenderer.RenderWorkspaceAsync"/> with <c>UseTreeRendering=true</c>)
/// behaves correctly at narrow (60-char) and standard (80-char) terminal widths.
/// Covers hierarchy rendering, title truncation, seeds, context markers,
/// depth limiting, and section headers.
/// </summary>
public sealed class WorkspaceTreeWidthTests
{
    private const string ShortTitle = "Fix login bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string MediumTitle = "Update user authentication flow for SSO";

    private static readonly IReadOnlyDictionary<string, int> TypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["User Story"] = 2,
            ["Task"] = 3,
        };

    // ── Narrow (60) — basic tree rendering ──────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_RendersWithoutCrash()
    {
        var story = new WorkItemBuilder(10, LongTitle).AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTree(60, new[] { story }, sections);

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("10");
    }

    [Fact]
    public async Task Tree_NarrowWidth_ShortTitleVisible()
    {
        var task = new WorkItemBuilder(20, ShortTitle).AsTask().InState("Active").Build();
        var roots = new[] { BuildNode(task, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { task }, roots);

        var output = await RenderTree(60, new[] { task }, sections);

        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task Tree_NarrowWidth_LongTitleTruncated()
    {
        var (console, renderer) = CreateTreeRenderer(60);
        var story = new WorkItemBuilder(30, LongTitle).AsUserStory().InState("Active").Build();

        var budget = new WidthBudget(60);
        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 0);

        label.ShouldContain("…");
        label.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public async Task Tree_NarrowWidth_HierarchyPreserved()
    {
        var epic = new WorkItemBuilder(10, "My Epic").AsEpic().InState("Active").Build();
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTree(60, new[] { story }, sections);

        output.ShouldContain("My Epic");
        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task Tree_NarrowWidth_MultipleItems_AllIdsVisible()
    {
        var items = new[]
        {
            new WorkItemBuilder(100, "Alpha Task").AsTask().InState("Active").Build(),
            new WorkItemBuilder(101, "Beta Task").AsTask().InState("New").Build(),
            new WorkItemBuilder(102, "Gamma Task").AsTask().InState("Closed").Build(),
        };

        var roots = items.Select(i => BuildNode(i, isSprintItem: true)).ToArray();
        var sections = BuildSectionsWithTree(items, roots);
        var output = await RenderTree(60, items, sections);

        output.ShouldContain("100");
        output.ShouldContain("101");
        output.ShouldContain("102");
    }

    // ── Narrow (60) — depth limiting ────────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_DepthLimiting_HidesDeepItems()
    {
        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var task = new WorkItemBuilder(30, "Visible Task").AsTask().InState("Active").Build();
        var subtask = new WorkItemBuilder(40, "Hidden Subtask").AsTask().InState("Active").Build();

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
        var output = await RenderTree(60, new[] { story, task, subtask }, sections, depthDown: 1);

        output.ShouldContain("Story");
        output.ShouldContain("Visible Task");
        output.ShouldContain("more");
    }

    // ── Narrow (60) — seeds ─────────────────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_Seeds_RenderedCorrectly()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "My Draft Seed").AsSeed().Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(60, new[] { story }, sections, new[] { seed });

        output.ShouldContain("My Draft Seed");
        output.ShouldContain("●");
    }

    [Fact]
    public async Task Tree_NarrowWidth_StaleSeed_ShowsMarker()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-2, "Old Seed").AsSeed(daysOld: 30).Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(60, new[] { story }, sections,
            new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
    }

    [Fact]
    public async Task Tree_NarrowWidth_Seeds_LongTitle_IsTruncated()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var seed = new WorkItemBuilder(-10, LongTitle).AsSeed().Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(60, new[] { story }, sections, new[] { seed });

        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public async Task Tree_WideWidth_Seeds_ShortTitle_NotTruncated()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var seed = new WorkItemBuilder(-11, MediumTitle).AsSeed().Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(120, new[] { story }, sections, new[] { seed });

        output.ShouldContain(MediumTitle);
    }

    // ── Narrow (60) — active context ────────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_ActiveContext_ShowsMarker()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWithContext(60, story, new[] { story }, sections);

        output.ShouldContain("►");
    }

    // ── Narrow (60) — working level dimming ─────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_AncestorAboveWorkingLevel_Dimmed()
    {
        var (console, renderer) = CreateTreeRenderer(60, workingLevel: "User Story");
        var epic = new WorkItemBuilder(10, "Context Epic").AsEpic().InState("Active").Build();

        var budget = new WidthBudget(60);
        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(epic, isSprintItem: false), null, 5, budget, 0);

        label.ShouldStartWith("[dim]");
        label.ShouldContain("Context Epic");
    }

    // ── Narrow (60) — progress footer ───────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_ProgressFooter_ShowsDoneCount()
    {
        var active = new WorkItemBuilder(20, "Active Story").AsUserStory().InState("Active").Build();
        var done = new WorkItemBuilder(21, "Done Story").AsUserStory().InState("Closed").Build();

        var roots = new[]
        {
            BuildNode(active, isSprintItem: true),
            BuildNode(done, isSprintItem: true),
        };
        var sections = BuildSectionsWithTree(new[] { active, done }, roots);
        var output = await RenderTree(60, new[] { active, done }, sections);

        output.ShouldContain("done");
    }

    // ── Narrow (60) — multiple sections ─────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_MultipleSections_ShowsSectionHeaders()
    {
        var sprintStory = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var areaTask = new WorkItemBuilder(30, "Area Task").AsTask().InState("Active").Build();

        var sprintRoots = new[] { BuildNode(sprintStory, isSprintItem: true) };
        var areaRoots = new[] { BuildNode(areaTask, isSprintItem: true) };

        var sections = WorkspaceSections.BuildWithTreeRoots(new WorkspaceSection[]
        {
            new("Sprint", new[] { sprintStory }, sprintRoots),
            new("Area", new[] { areaTask }, areaRoots),
        });

        var allItems = new[] { sprintStory, areaTask };
        var output = await RenderTree(60, allItems, sections);

        output.ShouldContain("Sprint Story");
        output.ShouldContain("Area Task");
    }

    // ── Narrow (60) — FormatLabel depth budget ──────────────────────

    [Fact]
    public void FormatLabel_NarrowWidth_DeeperDepth_ReducesBudget()
    {
        var (console, renderer) = CreateTreeRenderer(60);
        var budget = new WidthBudget(60);

        var title = new string('X', 40);
        var story = new WorkItemBuilder(10, title).AsUserStory().InState("Active").Build();

        var labelDepth0 = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 0);
        var labelDepth3 = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 3);

        // At narrow width, deeper depth should truncate more aggressively
        labelDepth3.ShouldContain("…");
    }

    // ── Standard (80) — basic tree rendering ────────────────────────

    [Fact]
    public async Task Tree_StandardWidth_RendersWithoutCrash()
    {
        var story = new WorkItemBuilder(10, LongTitle).AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTree(80, new[] { story }, sections);

        output.ShouldNotBeNullOrWhiteSpace();
        output.ShouldContain("10");
    }

    [Fact]
    public async Task Tree_StandardWidth_ShortTitleFullyVisible()
    {
        var task = new WorkItemBuilder(20, ShortTitle).AsTask().InState("Active").Build();
        var roots = new[] { BuildNode(task, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { task }, roots);

        var output = await RenderTree(80, new[] { task }, sections);

        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public async Task Tree_StandardWidth_LongTitleTruncated()
    {
        var (console, renderer) = CreateTreeRenderer(80);
        var story = new WorkItemBuilder(30, LongTitle).AsUserStory().InState("Active").Build();

        var budget = new WidthBudget(80);
        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 0);

        label.ShouldContain("…");
        label.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public async Task Tree_StandardWidth_MediumTitleFits()
    {
        var (console, renderer) = CreateTreeRenderer(80);
        var task = new WorkItemBuilder(40, MediumTitle).AsTask().InState("Active").Build();

        var budget = new WidthBudget(80);
        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(task, isSprintItem: true), null, 5, budget, 0);

        label.ShouldContain(MediumTitle);
        label.ShouldNotContain("…");
    }

    [Fact]
    public async Task Tree_StandardWidth_HierarchyPreserved()
    {
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
        var output = await RenderTree(80, new[] { story, task }, sections);

        output.ShouldContain("My Epic");
        output.ShouldContain("My Story");
        output.ShouldContain("My Task");
    }

    [Fact]
    public async Task Tree_StandardWidth_MultipleItems_AllIdsVisible()
    {
        var items = new[]
        {
            new WorkItemBuilder(100, "Alpha Task").AsTask().InState("Active").Build(),
            new WorkItemBuilder(101, "Beta Task").AsTask().InState("New").Build(),
        };

        var roots = items.Select(i => BuildNode(i, isSprintItem: true)).ToArray();
        var sections = BuildSectionsWithTree(items, roots);
        var output = await RenderTree(80, items, sections);

        output.ShouldContain("100");
        output.ShouldContain("101");
    }

    // ── Standard (80) — seeds ───────────────────────────────────────

    [Fact]
    public async Task Tree_StandardWidth_Seeds_RenderedCorrectly()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var seed = new WorkItemBuilder(-1, "Standard Seed").AsSeed().Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(80, new[] { story }, sections, new[] { seed });

        output.ShouldContain("Standard Seed");
        output.ShouldContain("●");
    }

    [Fact]
    public async Task Tree_StandardWidth_StaleSeed_ShowsMarker()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var staleSeed = new WorkItemBuilder(-3, "Stale Standard").AsSeed(daysOld: 30).Build();

        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var output = await RenderTreeWithSeeds(80, new[] { story }, sections,
            new[] { staleSeed }, staleDays: 14);

        output.ShouldContain("stale");
    }

    // ── Standard (80) — active context ──────────────────────────────

    [Fact]
    public async Task Tree_StandardWidth_ActiveContext_ShowsMarker()
    {
        var story = new WorkItemBuilder(20, ShortTitle).AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var output = await RenderTreeWithContext(80, story, new[] { story }, sections);

        output.ShouldContain("►");
    }

    // ── Standard (80) — progress footer ─────────────────────────────

    [Fact]
    public async Task Tree_StandardWidth_ProgressFooter_ShowsDoneCount()
    {
        var active = new WorkItemBuilder(20, "Active Story").AsUserStory().InState("Active").Build();
        var done = new WorkItemBuilder(21, "Done Story").AsUserStory().InState("Closed").Build();

        var roots = new[]
        {
            BuildNode(active, isSprintItem: true),
            BuildNode(done, isSprintItem: true),
        };
        var sections = BuildSectionsWithTree(new[] { active, done }, roots);
        var output = await RenderTree(80, new[] { active, done }, sections);

        output.ShouldContain("done");
    }

    // ── Standard (80) — depth limiting ──────────────────────────────

    [Fact]
    public async Task Tree_StandardWidth_DepthLimiting_HidesDeepItems()
    {
        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var task = new WorkItemBuilder(30, "Visible Task").AsTask().InState("Active").Build();
        var subtask = new WorkItemBuilder(40, "Hidden Subtask").AsTask().InState("Active").Build();

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
        var output = await RenderTree(80, new[] { story, task, subtask }, sections, depthDown: 1);

        output.ShouldContain("Story");
        output.ShouldContain("Visible Task");
        output.ShouldContain("more");
    }

    // ── Standard (80) — working level dimming ───────────────────────

    [Fact]
    public async Task Tree_StandardWidth_AncestorAboveWorkingLevel_Dimmed()
    {
        var (console, renderer) = CreateTreeRenderer(80, workingLevel: "User Story");
        var epic = new WorkItemBuilder(10, "Context Epic").AsEpic().InState("Active").Build();

        var budget = new WidthBudget(80);
        var label = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(epic, isSprintItem: false), null, 5, budget, 0);

        label.ShouldStartWith("[dim]");
        label.ShouldContain("Context Epic");
    }

    // ── Standard (80) — FormatLabel depth budget ────────────────────

    [Fact]
    public void FormatLabel_StandardWidth_DeeperDepth_ReducesBudget()
    {
        var (console, renderer) = CreateTreeRenderer(80);
        var budget = new WidthBudget(80);

        var title = new string('X', 50);
        var story = new WorkItemBuilder(10, title).AsUserStory().InState("Active").Build();

        var labelDepth0 = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 0);
        var labelDepth5 = renderer.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, budget, 5);

        labelDepth0.ShouldNotContain("…");
        labelDepth5.ShouldContain("…");
    }

    // ── Width comparison ────────────────────────────────────────────

    [Fact]
    public async Task Tree_NarrowVsStandard_BothContainId()
    {
        var story = new WorkItemBuilder(50, LongTitle).AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);

        var narrow = await RenderTree(60, new[] { story }, sections);
        var standard = await RenderTree(80, new[] { story }, sections);

        narrow.ShouldContain("50");
        standard.ShouldContain("50");
    }

    [Fact]
    public void FormatLabel_NarrowTruncatesMore_ThanStandard()
    {
        var title = new string('A', 60);
        var story = new WorkItemBuilder(10, title).AsUserStory().InState("Active").Build();

        var (_, narrow) = CreateTreeRenderer(60);
        var narrowBudget = new WidthBudget(60);
        var narrowLabel = narrow.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, narrowBudget, 0);

        var (_, standard) = CreateTreeRenderer(80);
        var stdBudget = new WidthBudget(80);
        var stdLabel = standard.FormatWorkspaceTreeNodeLabel(
            BuildNode(story, isSprintItem: true), null, 5, stdBudget, 0);

        // Both should truncate a 60-char title, but narrow should truncate more
        narrowLabel.ShouldContain("…");
        stdLabel.ShouldContain("…");
        // Narrow markup text (excluding tags) should be shorter than standard
        narrowLabel.Length.ShouldBeLessThan(stdLabel.Length);
    }

    // ── Virtual group nodes ─────────────────────────────────────────

    [Fact]
    public async Task Tree_NarrowWidth_VirtualGroup_RenderedCorrectly()
    {
        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[]
        {
            BuildNode(task, isSprintItem: true)
        });

        var sections = BuildSectionsWithTree(new[] { task }, new[] { virtualRoot });
        var output = await RenderTree(60, new[] { task }, sections);

        output.ShouldContain("Unparented Tasks");
        output.ShouldContain("Orphan Task");
    }

    [Fact]
    public async Task Tree_StandardWidth_VirtualGroup_RenderedCorrectly()
    {
        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[]
        {
            BuildNode(task, isSprintItem: true)
        });

        var sections = BuildSectionsWithTree(new[] { task }, new[] { virtualRoot });
        var output = await RenderTree(80, new[] { task }, sections);

        output.ShouldContain("Unparented Tasks");
        output.ShouldContain("Orphan Task");
    }

    // ── Flat fallback title truncation ─────────────────────────────

    [Fact]
    public async Task FlatFallback_NarrowWidth_LongTitleTruncated()
    {
        var item = new WorkItemBuilder(10, LongTitle).AsUserStory().InState("Active").Build();
        var output = await RenderFlat(60, new[] { item });

        output.ShouldContain("10");
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public async Task FlatFallback_NarrowWidth_ShortTitleNotTruncated()
    {
        var item = new WorkItemBuilder(20, ShortTitle).AsTask().InState("Active").Build();
        var output = await RenderFlat(60, new[] { item });

        output.ShouldContain(ShortTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task FlatFallback_StandardWidth_LongTitleTruncated()
    {
        var item = new WorkItemBuilder(30, LongTitle).AsUserStory().InState("Active").Build();
        var output = await RenderFlat(80, new[] { item });

        output.ShouldContain("30");
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public async Task FlatFallback_StandardWidth_MediumTitleFits()
    {
        var item = new WorkItemBuilder(40, MediumTitle).AsTask().InState("Active").Build();
        var output = await RenderFlat(80, new[] { item });

        output.ShouldContain(MediumTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task FlatFallback_WideWidth_LongTitleFits()
    {
        var item = new WorkItemBuilder(50, LongTitle).AsUserStory().InState("Active").Build();
        var output = await RenderFlat(200, new[] { item });

        output.ShouldContain(LongTitle);
        output.ShouldNotContain("…");
    }

    [Fact]
    public async Task FlatFallback_NarrowVsStandard_NarrowTruncatesMore()
    {
        var title = new string('Z', 60);
        var item = new WorkItemBuilder(60, title).AsTask().InState("Active").Build();

        var narrowOutput = await RenderFlat(60, new[] { item });
        var stdOutput = await RenderFlat(80, new[] { item });

        narrowOutput.ShouldContain("…");
        stdOutput.ShouldContain("…");
        // Narrow output should be shorter due to more aggressive truncation
        narrowOutput.Length.ShouldBeLessThan(stdOutput.Length);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (TestConsole Console, SpectreRenderer Renderer) CreateTreeRenderer(
        int width, string? workingLevel = null)
    {
        var console = new TestConsole { Profile = { Width = width } };
        var theme = new SpectreTheme(new DisplayConfig());
        var renderer = new SpectreRenderer(console, theme)
        {
            UseTreeRendering = true,
            TreeDepthUp = 2,
            TreeDepthDown = 10,
            TreeDepthSideways = 1,
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

    private static async Task<string> RenderTree(
        int width, WorkItem[] sprintItems, WorkspaceSections sections,
        int depthDown = 10)
    {
        var (console, renderer) = CreateTreeRenderer(width);
        renderer.TreeDepthDown = depthDown;

        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(sprintItems, sections),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderFlat(
        int width, WorkItem[] sprintItems)
    {
        var (console, renderer) = CreateTreeRenderer(width);

        // SprintItemsLoaded with null Sections triggers the flat fallback path
        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(sprintItems),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderTreeWithSeeds(
        int width, WorkItem[] sprintItems, WorkspaceSections sections,
        WorkItem[] seeds, int staleDays = 14)
    {
        var (console, renderer) = CreateTreeRenderer(width);

        var chunks = CreateChunksAsync(
            new ContextLoaded(null),
            new SprintItemsLoaded(sprintItems, sections),
            new SeedsLoaded(seeds));

        await renderer.RenderWorkspaceAsync(chunks, staleDays, false, CancellationToken.None);
        return console.Output;
    }

    private static async Task<string> RenderTreeWithContext(
        int width, WorkItem contextItem, WorkItem[] sprintItems, WorkspaceSections sections)
    {
        var (console, renderer) = CreateTreeRenderer(width);

        var chunks = CreateChunksAsync(
            new ContextLoaded(contextItem),
            new SprintItemsLoaded(sprintItems, sections),
            new SeedsLoaded(Array.Empty<WorkItem>()));

        await renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
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
}
