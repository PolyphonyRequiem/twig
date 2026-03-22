using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

// ── EPIC-2: SprintHierarchy unit tests ─────────────────────
public class SprintHierarchyTests
{
    // ═══════════════════════════════════════════════════════════════
    //  (1) Two tasks under same Feature → Feature node with two children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_TwoTasksUnderSameFeature_FeatureNodeWithTwoChildren()
    {
        var feature = MakeItem(100, "Feature A", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Alice");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });

        hierarchy.AssigneeGroups.ShouldContainKey("Alice");
        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.Count.ShouldBe(1);

        var featureNode = roots[0];
        featureNode.Item.Id.ShouldBe(100);
        featureNode.IsSprintItem.ShouldBeFalse();
        featureNode.Children.Count.ShouldBe(2);
        featureNode.Children.ShouldContain(n => n.Item.Id == 1 && n.IsSprintItem);
        featureNode.Children.ShouldContain(n => n.Item.Id == 2 && n.IsSprintItem);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (2) Items with no parents → root level
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ItemsWithNoParents_AtRootLevel()
    {
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: null, assignee: "Bob");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: null, assignee: "Bob");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });

        var roots = hierarchy.AssigneeGroups["Bob"];
        roots.Count.ShouldBe(2);
        roots.ShouldAllBe(n => n.IsSprintItem);
        roots.ShouldAllBe(n => n.Children.Count == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (3) Items at different hierarchy levels
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ItemsAtDifferentLevels_CorrectTree()
    {
        // Epic → Feature → UserStory (sprint item)
        //                → Task (sprint item)
        var epic = MakeItem(1000, "Epic", WorkItemType.Epic, parentId: null);
        var feature = MakeItem(100, "Feature", WorkItemType.Feature, parentId: 1000);
        var story = MakeItem(10, "User Story", WorkItemType.UserStory, parentId: 100, assignee: "Carol");
        var task = MakeItem(11, "Task", WorkItemType.Task, parentId: 100, assignee: "Carol");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1000] = epic,
            [100] = feature,
        };

        // Ceiling at Epic → include Feature as context, stop at Epic
        var hierarchy = SprintHierarchy.Build(new[] { story, task }, parentLookup, new[] { "Epic" });

        var roots = hierarchy.AssigneeGroups["Carol"];
        roots.Count.ShouldBe(1); // Epic is the root

        var epicNode = roots[0];
        epicNode.Item.Id.ShouldBe(1000);
        epicNode.IsSprintItem.ShouldBeFalse();

        epicNode.Children.Count.ShouldBe(1); // Feature
        var featureNode = epicNode.Children[0];
        featureNode.Item.Id.ShouldBe(100);
        featureNode.IsSprintItem.ShouldBeFalse();

        featureNode.Children.Count.ShouldBe(2); // Story + Task
    }

    // ═══════════════════════════════════════════════════════════════
    //  (4) Empty sprint → empty hierarchy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptySprint_EmptyHierarchy()
    {
        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(Array.Empty<WorkItem>(), parentLookup, new[] { "Feature" });

        hierarchy.AssigneeGroups.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (5) Single item sprint
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SingleItem_SingleRoot()
    {
        var task = MakeItem(1, "Only task", WorkItemType.Task, parentId: null, assignee: "Dan");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task }, parentLookup, new[] { "Feature" });

        var roots = hierarchy.AssigneeGroups["Dan"];
        roots.Count.ShouldBe(1);
        roots[0].Item.Id.ShouldBe(1);
        roots[0].IsSprintItem.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (6) Item whose parent is also a sprint item → parent IsSprintItem=true
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ParentIsAlsoSprintItem_ParentMarkedAsSprintItem()
    {
        var feature = MakeItem(100, "Feature", WorkItemType.Feature, parentId: null);
        var story = MakeItem(10, "User Story", WorkItemType.UserStory, parentId: 100, assignee: "Eve");
        var task = MakeItem(1, "Task", WorkItemType.Task, parentId: 10, assignee: "Eve");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [100] = feature,
            [10] = story,
        };

        // Both story and task are sprint items; story is parent of task
        var hierarchy = SprintHierarchy.Build(new[] { story, task }, parentLookup, new[] { "Feature" });

        var roots = hierarchy.AssigneeGroups["Eve"];
        roots.Count.ShouldBe(1);

        var featureNode = roots[0];
        featureNode.Item.Id.ShouldBe(100);
        featureNode.IsSprintItem.ShouldBeFalse();

        var storyNode = featureNode.Children.ShouldHaveSingleItem();
        storyNode.Item.Id.ShouldBe(10);
        storyNode.IsSprintItem.ShouldBeTrue(); // story is a sprint item AND a parent

        var taskNode = storyNode.Children.ShouldHaveSingleItem();
        taskNode.Item.Id.ShouldBe(1);
        taskNode.IsSprintItem.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (7) Multiple assignees with shared and distinct parents
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_MultipleAssignees_SeparateGroups()
    {
        var feature = MakeItem(100, "Feature A", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Alice");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Bob");
        var task3 = MakeItem(3, "Task 3", WorkItemType.Task, parentId: null, assignee: "Bob");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2, task3 }, parentLookup, new[] { "Feature" });

        hierarchy.AssigneeGroups.Count.ShouldBe(2);

        // Alice: Feature → Task 1
        var aliceRoots = hierarchy.AssigneeGroups["Alice"];
        aliceRoots.Count.ShouldBe(1);
        aliceRoots[0].Item.Id.ShouldBe(100);
        aliceRoots[0].Children.ShouldHaveSingleItem().Item.Id.ShouldBe(1);

        // Bob: Feature → Task 2, Task 3 at root
        var bobRoots = hierarchy.AssigneeGroups["Bob"];
        bobRoots.Count.ShouldBe(2); // Feature node + Task 3

        bobRoots.ShouldContain(n => n.Item.Id == 100);
        bobRoots.ShouldContain(n => n.Item.Id == 3 && n.IsSprintItem);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (8) Parent not in parentLookup → item at root
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ParentNotInLookup_ItemAtRoot()
    {
        // Task has parentId=999 but that's not in the lookup
        var task = MakeItem(1, "Orphan task", WorkItemType.Task, parentId: 999, assignee: "Frank");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task }, parentLookup, new[] { "Feature" });

        var roots = hierarchy.AssigneeGroups["Frank"];
        roots.ShouldHaveSingleItem();
        roots[0].Item.Id.ShouldBe(1);
        roots[0].IsSprintItem.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  (9) Ceiling is null → no parent context, items flat
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_CeilingNull_ItemsFlat()
    {
        var feature = MakeItem(100, "Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Grace");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Grace");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, ceilingTypeNames: null);

        var roots = hierarchy.AssigneeGroups["Grace"];
        roots.Count.ShouldBe(2);
        roots.ShouldAllBe(n => n.IsSprintItem);
        roots.ShouldAllBe(n => n.Children.Count == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (9b) Ceiling is empty list → same as null, items flat
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptyCeilingTypeNames_ItemsFlat()
    {
        var feature = MakeItem(100, "Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Grace");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Grace");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, ceilingTypeNames: Array.Empty<string>());

        var roots = hierarchy.AssigneeGroups["Grace"];
        roots.Count.ShouldBe(2);
        roots.ShouldAllBe(n => n.IsSprintItem);
        roots.ShouldAllBe(n => n.Children.Count == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (10) Parent chain exceeds ceiling → trimmed at ceiling
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ParentChainExceedsCeiling_TrimmedAtCeiling()
    {
        // Epic → Feature → UserStory (sprint), ceiling = "Feature"
        // Should show: Feature → UserStory. Epic excluded.
        var epic = MakeItem(1000, "Epic", WorkItemType.Epic, parentId: null);
        var feature = MakeItem(100, "Feature A", WorkItemType.Feature, parentId: 1000);
        var story = MakeItem(10, "Story 1", WorkItemType.UserStory, parentId: 100, assignee: "Hank");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1000] = epic,
            [100] = feature,
        };

        var hierarchy = SprintHierarchy.Build(new[] { story }, parentLookup, new[] { "Feature" });

        var roots = hierarchy.AssigneeGroups["Hank"];
        roots.Count.ShouldBe(1);

        // Root should be the Feature (ceiling), not the Epic
        var featureNode = roots[0];
        featureNode.Item.Id.ShouldBe(100);
        featureNode.IsSprintItem.ShouldBeFalse();

        featureNode.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(10);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (11) Ceiling with multiple type names — parent of second type included
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_CeilingMultipleTypeNames_ParentOfSecondTypeIncluded()
    {
        // Ceiling types: ["User Story", "Backlog Item"]
        // Parent is a "Backlog Item" — should be recognized as ceiling and included
        var backlogItem = MakeItem(200, "Backlog Item 1", WorkItemType.Parse("Backlog Item").Value, parentId: null);
        var task = MakeItem(1, "Task 1", WorkItemType.Task, parentId: 200, assignee: "Ivy");

        var parentLookup = new Dictionary<int, WorkItem> { [200] = backlogItem };
        var hierarchy = SprintHierarchy.Build(
            new[] { task },
            parentLookup,
            new[] { "User Story", "Backlog Item" });

        var roots = hierarchy.AssigneeGroups["Ivy"];
        roots.Count.ShouldBe(1);

        var backlogNode = roots[0];
        backlogNode.Item.Id.ShouldBe(200);
        backlogNode.IsSprintItem.ShouldBeFalse();
        backlogNode.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  (12) AssigneeGroups are alphabetically ordered (case-insensitive)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_MultipleAssignees_AlphabeticallyOrdered()
    {
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: null, assignee: "Zara");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: null, assignee: "Alice");
        var task3 = MakeItem(3, "Task 3", WorkItemType.Task, parentId: null, assignee: "bob");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2, task3 }, parentLookup, new[] { "Feature" });

        var keys = hierarchy.AssigneeGroups.Keys.ToList();
        keys.Count.ShouldBe(3);
        keys[0].ShouldBe("Alice");
        keys[1].ShouldBe("bob");
        keys[2].ShouldBe("Zara");
    }

    // ═══════════════════════════════════════════════════════════════
    //  (13) Unassigned items use "(unassigned)" key
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_UnassignedItems_GroupedUnderUnassigned()
    {
        var task = MakeItem(1, "Task 1", WorkItemType.Task, parentId: null, assignee: null);

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task }, parentLookup, new[] { "Feature" });

        hierarchy.AssigneeGroups.ShouldContainKey("(unassigned)");
        hierarchy.AssigneeGroups["(unassigned)"].ShouldHaveSingleItem().Item.Id.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-005: Virtual group tests for unparented items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_UnparentedTaskWithTypeLevelMap_CreatesVirtualGroup()
    {
        var task1 = MakeItem(1, "Task 1", WorkItemType.Task, parentId: null, assignee: "Alice");
        var task2 = MakeItem(2, "Task 2", WorkItemType.Task, parentId: null, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.Count.ShouldBe(1); // One virtual group

        var virtualGroup = roots[0];
        virtualGroup.IsVirtualGroup.ShouldBeTrue();
        virtualGroup.GroupLabel.ShouldBe("Unparented Tasks");
        virtualGroup.BacklogLevel.ShouldBe(2);
        virtualGroup.Children.Count.ShouldBe(2);
        virtualGroup.Children.ShouldAllBe(n => n.IsSprintItem);
    }

    [Fact]
    public void Build_UnparentedFeatureWithTypeLevelMap_CreatesVirtualGroup()
    {
        var feature = MakeItem(1, "Dark Mode", WorkItemType.Feature, parentId: null, assignee: "Bob");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(new[] { feature }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Bob"];
        roots.Count.ShouldBe(1);

        var virtualGroup = roots[0];
        virtualGroup.IsVirtualGroup.ShouldBeTrue();
        virtualGroup.GroupLabel.ShouldBe("Unparented Features");
        virtualGroup.BacklogLevel.ShouldBe(1);
        virtualGroup.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(1);
    }

    [Fact]
    public void Build_MixOfParentedAndUnparentedItems_SeparatesCorrectly()
    {
        var epic = MakeItem(1000, "Payment Refactor", WorkItemType.Epic, parentId: null);
        var feature = MakeItem(100, "Retry Logic", WorkItemType.Feature, parentId: 1000);
        var task1 = MakeItem(10, "Add timeout", WorkItemType.Task, parentId: 100, assignee: "Alice");
        // Unparented task
        var task2 = MakeItem(20, "Update docs", WorkItemType.Task, parentId: null, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1000] = epic,
            [100] = feature,
        };
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(
            new[] { task1, task2 }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        // Should have: Epic subtree (parented) + "Unparented Tasks" virtual group
        roots.Count.ShouldBe(2);

        // First root should be the parented subtree (Epic → Feature → Task)
        var parentedRoot = roots.First(r => !r.IsVirtualGroup);
        parentedRoot.Item.Id.ShouldBe(1000);

        // Second root should be the virtual group
        var virtualGroup = roots.First(r => r.IsVirtualGroup);
        virtualGroup.GroupLabel.ShouldBe("Unparented Tasks");
        virtualGroup.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(20);
    }

    [Fact]
    public void Build_MultipleUnparentedLevels_CreatesMultipleVirtualGroups()
    {
        var feature = MakeItem(1, "Dark Mode", WorkItemType.Feature, parentId: null, assignee: "Alice");
        var task = MakeItem(2, "Update docs", WorkItemType.Task, parentId: null, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(
            new[] { feature, task }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.Count.ShouldBe(2); // Two virtual groups: Features and Tasks

        var featureGroup = roots.First(r => r.IsVirtualGroup && r.GroupLabel == "Unparented Features");
        featureGroup.BacklogLevel.ShouldBe(1);
        featureGroup.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(1);

        var taskGroup = roots.First(r => r.IsVirtualGroup && r.GroupLabel == "Unparented Tasks");
        taskGroup.BacklogLevel.ShouldBe(2);
        taskGroup.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(2);
    }

    [Fact]
    public void Build_NoTypeLevelMap_NoVirtualGroups()
    {
        var task = MakeItem(1, "Task 1", WorkItemType.Task, parentId: null, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(
            new[] { task }, parentLookup, new[] { "Feature" }, typeLevelMap: null);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.ShouldHaveSingleItem();
        roots[0].IsVirtualGroup.ShouldBeFalse();
    }

    [Fact]
    public void Build_ContextParentWithChildren_NotGrouped()
    {
        // Epic is a context parent (not a sprint item), should NOT be grouped
        var epic = MakeItem(1000, "Payment Refactor", WorkItemType.Epic, parentId: null);
        var task = MakeItem(10, "Add timeout", WorkItemType.Task, parentId: 1000, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem> { [1000] = epic };
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Task"] = 1,
        };

        var hierarchy = SprintHierarchy.Build(
            new[] { task }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.ShouldHaveSingleItem();
        var root = roots[0];
        root.IsVirtualGroup.ShouldBeFalse();
        root.Item.Id.ShouldBe(1000); // Epic is context parent, not grouped
        root.IsSprintItem.ShouldBeFalse();
    }

    [Fact]
    public void Build_UnparentedEpic_GroupedUnderVirtualHeader()
    {
        var epic = MakeItem(1, "Observability", WorkItemType.Epic, parentId: null, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(
            new[] { epic }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.ShouldHaveSingleItem();
        var group = roots[0];
        group.IsVirtualGroup.ShouldBeTrue();
        group.GroupLabel.ShouldBe("Unparented Epics");
        group.BacklogLevel.ShouldBe(0);
        group.Children.ShouldHaveSingleItem().Item.Id.ShouldBe(1);
    }

    [Fact]
    public void Build_ItemWithParentIdButParentMissingFromLookup_NotGroupedAsVirtual()
    {
        // Task has ParentId=999 but that parent is not in the lookup.
        // The item ends up as a root with ParentId.HasValue == true,
        // so it should NOT be grouped under a virtual header.
        var task = MakeItem(1, "Orphan task", WorkItemType.Task, parentId: 999, assignee: "Alice");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Feature"] = 1,
            ["Task"] = 2,
        };

        var hierarchy = SprintHierarchy.Build(
            new[] { task }, parentLookup, new[] { "Epic" }, typeLevelMap);

        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.ShouldHaveSingleItem();
        var root = roots[0];
        root.IsVirtualGroup.ShouldBeFalse();
        root.Item.Id.ShouldBe(1);
        root.IsSprintItem.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(
        int id,
        string title,
        WorkItemType type,
        int? parentId,
        string? assignee = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = "New",
            ParentId = parentId,
            AssignedTo = assignee,
        };
    }
}
