using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for tree-based workspace rendering in HumanOutputFormatter.
/// Covers hierarchical layout, working-level focus (dimmed ancestors, bold sprint items),
/// depth limiting (TreeDepthUp/Down/Sideways), virtual group rendering, and flat fallback.
/// Mirrors the SpectreRenderer tree rendering tests for consistency.
/// </summary>
public sealed class HumanOutputFormatterTreeTests
{
    private static readonly IReadOnlyDictionary<string, int> TypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["User Story"] = 2,
            ["Task"] = 3,
        };

    private const string Dim = "\x1b[2m";
    private const string Bold = "\x1b[1m";
    private const string Cyan = "\x1b[36m";
    private const string Yellow = "\x1b[33m";
    private const string Reset = "\x1b[0m";

    // ── Tree rendering basics ───────────────────────────────────────

    [Fact]
    public void TreeMode_RendersHierarchyFromTreeRoots()
    {
        var formatter = CreateTreeFormatter();

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
        var ws = Workspace.Build(null, new[] { story, task }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("My Epic");
        output.ShouldContain("My Story");
        output.ShouldContain("My Task");
    }

    [Fact]
    public void TreeMode_SprintItemsAreBold()
    {
        var formatter = CreateTreeFormatter();

        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Sprint items should contain bold formatting and ID
        output.ShouldContain($"{Bold}Sprint Story{Reset}");
        output.ShouldContain("#20");
    }

    [Fact]
    public void TreeMode_ContextAncestorDimmedTitle()
    {
        var formatter = CreateTreeFormatter();

        var epic = new WorkItemBuilder(10, "Parent Epic").AsEpic().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Sprint Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Non-sprint context item has dimmed title
        output.ShouldContain($"{Dim}Parent Epic{Reset}");
    }

    // ── Working-level focus ─────────────────────────────────────────

    [Fact]
    public void TreeMode_AboveWorkingLevel_FullyDimmed()
    {
        var formatter = CreateTreeFormatter(workingLevel: "User Story");

        var epic = new WorkItemBuilder(10, "Dim Epic").AsEpic().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Focus Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Epic (above working level) should be fully dimmed
        output.ShouldContain($"{Dim}");
        output.ShouldContain("Dim Epic");
        // Story (at working level) should be bold
        output.ShouldContain($"{Bold}Focus Story{Reset}");
    }

    [Fact]
    public void TreeMode_BelowWorkingLevel_NotDimmed()
    {
        var formatter = CreateTreeFormatter(workingLevel: "User Story");

        var task = new WorkItemBuilder(30, "My Task").AsTask().InState("Active").Build();
        var roots = new[] { BuildNode(task, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { task }, roots);
        var ws = Workspace.Build(null, new[] { task }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Task is below working level — should still have bold for sprint item
        output.ShouldContain($"{Bold}My Task{Reset}");
    }

    // ── Depth limiting ──────────────────────────────────────────────

    [Fact]
    public void TreeMode_DepthDown_TruncatesAndShowsIndicator()
    {
        var formatter = CreateTreeFormatter(depthDown: 1);

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
        var ws = Workspace.Build(null, new[] { story, task, subtask }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("Story");
        output.ShouldContain("Task");
        output.ShouldNotContain("SubTask");
        output.ShouldContain("1 more");
    }

    [Fact]
    public void TreeMode_DepthSidewaysZero_HidesTruncationIndicator()
    {
        var formatter = CreateTreeFormatter(depthDown: 1, depthSideways: 0);

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
        var ws = Workspace.Build(null, new[] { story, task, subtask }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldNotContain("more");
    }

    [Fact]
    public void TreeMode_DepthUp_PrunesDistantAncestors()
    {
        var formatter = CreateTreeFormatter(workingLevel: "User Story", depthUp: 0);

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
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldNotContain("Pruned Epic");
        output.ShouldNotContain("Pruned Feature");
        output.ShouldContain("Only Story");
    }

    [Fact]
    public void TreeMode_DepthUp_KeepsWithinLimit()
    {
        var formatter = CreateTreeFormatter(workingLevel: "User Story", depthUp: 1);

        var epic = new WorkItemBuilder(10, "Pruned Epic").AsEpic().InState("Active").Build();
        var feature = new WorkItemBuilder(15, "Kept Feature").AsFeature().InState("Active").Build();
        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();

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
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Epic is 2 levels above User Story (working level), depthUp=1, so pruned
        output.ShouldNotContain("Pruned Epic");
        // Feature is 1 level above, within limit
        output.ShouldContain("Kept Feature");
        output.ShouldContain("Story");
    }

    // ── PruneAncestorsAboveDepthUp unit tests ───────────────────────

    [Fact]
    public void PruneAncestors_NoTypeLevelMap_ReturnsOriginalRoots()
    {
        var formatter = new HumanOutputFormatter();
        formatter.UseTreeRendering = true;
        // No TypeLevelMap or WorkingLevelTypeName set

        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };

        var result = formatter.PruneAncestorsAboveDepthUp(roots);
        result.ShouldBe(roots);
    }

    [Fact]
    public void PruneAncestors_VirtualGroupsPreserved()
    {
        var formatter = CreateTreeFormatter(workingLevel: "User Story", depthUp: 0);

        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[] { BuildNode(task, isSprintItem: true) });

        var result = formatter.PruneAncestorsAboveDepthUp(new[] { virtualRoot });
        result.Count.ShouldBe(1);
        result[0].IsVirtualGroup.ShouldBeTrue();
    }

    // ── Virtual group rendering ─────────────────────────────────────

    [Fact]
    public void TreeMode_VirtualGroupNode_RenderedDim()
    {
        var formatter = CreateTreeFormatter();

        var task = new WorkItemBuilder(30, "Orphan Task").AsTask().InState("Active").Build();
        var virtualRoot = BuildVirtualGroupNode("Unparented Tasks", new[]
        {
            BuildNode(task, isSprintItem: true)
        });

        var sections = BuildSectionsWithTree(new[] { task }, new[] { virtualRoot });
        var ws = Workspace.Build(null, new[] { task }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("Unparented Tasks");
        output.ShouldContain("Orphan Task");
    }

    // ── Active context and tracked items ────────────────────────────

    [Fact]
    public void TreeMode_ActiveItem_ShowsActiveMarker()
    {
        var formatter = CreateTreeFormatter();

        var story = new WorkItemBuilder(20, "Active Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(story, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain($"{Cyan}►{Reset}");
    }

    [Fact]
    public void TreeMode_TrackedItem_ShowsPinnedMarker()
    {
        var formatter = CreateTreeFormatter();

        var story = new WorkItemBuilder(20, "Tracked Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var trackedItems = new[] { new TrackedItem(20, TrackingMode.Single, DateTimeOffset.UtcNow) };
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections, trackedItems: trackedItems);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain($"{Yellow}📌{Reset}");
    }

    // ── Box-drawing characters ──────────────────────────────────────

    [Fact]
    public void TreeMode_MultipleChildren_UsesBoxDrawing()
    {
        var formatter = CreateTreeFormatter();

        var epic = new WorkItemBuilder(10, "Parent Epic").AsEpic().InState("Active").Build();
        var story1 = new WorkItemBuilder(20, "Story One").AsUserStory().InState("Active").Build();
        var story2 = new WorkItemBuilder(21, "Story Two").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story1, isSprintItem: true),
                BuildNode(story2, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story1, story2 }, roots);
        var ws = Workspace.Build(null, new[] { story1, story2 }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("├── ");
        output.ShouldContain("└── ");
    }

    // ── Fallback when tree rendering disabled ───────────────────────

    [Fact]
    public void NoTreeRendering_FallsBackToCategoryGrouping()
    {
        var formatter = new HumanOutputFormatter();
        // UseTreeRendering is false by default

        var item1 = new WorkItemBuilder(1, "Active Task").AsTask().InState("Active").Build();
        var item2 = new WorkItemBuilder(2, "New Task").AsTask().Build();
        var roots = new[] { BuildNode(item1, isSprintItem: true), BuildNode(item2, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { item1, item2 }, roots);
        var ws = Workspace.Build(null, new[] { item1, item2 }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Without tree rendering, falls back to state category grouping
        output.ShouldContain("In Progress");
        output.ShouldContain("Proposed");
    }

    [Fact]
    public void TreeMode_NoTreeRootsOnSection_FallsBackToCategoryGrouping()
    {
        var formatter = CreateTreeFormatter();

        var item = new WorkItemBuilder(1, "Task").AsTask().InState("Active").Build();
        // Sections without tree roots
        var sections = WorkspaceSections.Build(new[] { item });
        var ws = Workspace.Build(null, new[] { item }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Falls back to category grouping when no tree roots
        output.ShouldContain("In Progress");
    }

    // ── Progress indicators in tree mode ────────────────────────────

    [Fact]
    public void TreeMode_ParentWithChildren_ShowsProgressIndicator()
    {
        var formatter = CreateTreeFormatter();

        var epic = new WorkItemBuilder(10, "Parent Epic").AsEpic().InState("Active").Build();
        var story1 = new WorkItemBuilder(20, "Done Story").AsUserStory().InState("Closed").Build();
        var story2 = new WorkItemBuilder(21, "Active Story").AsUserStory().InState("Active").Build();

        var roots = new[]
        {
            BuildNode(epic, isSprintItem: false, children: new[]
            {
                BuildNode(story1, isSprintItem: true),
                BuildNode(story2, isSprintItem: true)
            })
        };

        var sections = BuildSectionsWithTree(new[] { story1, story2 }, roots);
        var ws = Workspace.Build(null, new[] { story1, story2 }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Progress indicator like [1/2]
        output.ShouldContain("[1/2]");
    }

    // ── Section headers with tree roots ─────────────────────────────

    [Fact]
    public void TreeMode_SingleSection_NoSectionHeader()
    {
        var formatter = CreateTreeFormatter();

        var story = new WorkItemBuilder(20, "Story").AsUserStory().InState("Active").Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        // Single section uses "Sprint (N items):" not "── Sprint (N) ──"
        output.ShouldContain("Sprint (1 items):");
        output.ShouldNotContain("── Sprint");
    }

    // ── Dirty items in tree mode ────────────────────────────────────

    [Fact]
    public void TreeMode_DirtySprintItem_ShowsDirtyMarker()
    {
        var formatter = CreateTreeFormatter();

        var story = new WorkItemBuilder(20, "Dirty Story").AsUserStory().InState("Active").Dirty().Build();
        var roots = new[] { BuildNode(story, isSprintItem: true) };
        var sections = BuildSectionsWithTree(new[] { story }, roots);
        var ws = Workspace.Build(null, new[] { story }, Array.Empty<WorkItem>(),
            sections: sections);

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain($"{Yellow}✎{Reset}");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static HumanOutputFormatter CreateTreeFormatter(
        string? workingLevel = null,
        int depthUp = 2,
        int depthDown = 10,
        int depthSideways = 1)
    {
        var formatter = new HumanOutputFormatter
        {
            UseTreeRendering = true,
            TreeDepthUp = depthUp,
            TreeDepthDown = depthDown,
            TreeDepthSideways = depthSideways,
        };

        if (workingLevel is not null)
        {
            formatter.TypeLevelMap = TypeLevelMap;
            formatter.WorkingLevelTypeName = workingLevel;
        }

        return formatter;
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
}
