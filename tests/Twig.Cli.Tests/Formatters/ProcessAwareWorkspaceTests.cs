using System.Text.RegularExpressions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for EPIC-003: Process-aware workspace display.
/// Covers state category grouping, progress indicators, assignee column,
/// and hierarchy indentation in both workspace and sprint views.
/// </summary>
public class ProcessAwareWorkspaceTests
{
    private const string Esc = "\x1b[";
    private readonly HumanOutputFormatter _formatter = new();

    // ═══════════════════════════════════════════════════════════════════
    // Task 1: State category grouping
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkspaceView_GroupsItemsByStateCategory()
    {
        var items = new[]
        {
            MakeItem(1, "New task", WorkItemType.Task, state: "New"),
            MakeItem(2, "Active task", WorkItemType.Task, state: "Active"),
            MakeItem(3, "Done task", WorkItemType.Task, state: "Closed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("Proposed");
        output.ShouldContain("In Progress");
        output.ShouldContain("Completed");
    }

    [Fact]
    public void SprintView_GroupsItemsByStateCategory()
    {
        var items = new[]
        {
            MakeItem(1, "New task", WorkItemType.Task, state: "New", assignee: "Alice"),
            MakeItem(2, "Active task", WorkItemType.Task, state: "Active", assignee: "Bob"),
            MakeItem(3, "Resolved task", WorkItemType.Task, state: "Resolved", assignee: "Alice"),
            MakeItem(4, "Done task", WorkItemType.Task, state: "Closed", assignee: "Bob"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("Proposed");
        output.ShouldContain("In Progress");
        output.ShouldContain("Resolved");
        output.ShouldContain("Completed");
    }

    [Fact]
    public void StateCategories_EmptyCategoriesAreOmitted()
    {
        var items = new[]
        {
            MakeItem(1, "Active task", WorkItemType.Task, state: "Active"),
            MakeItem(2, "Another active", WorkItemType.Task, state: "Active"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("In Progress");
        output.ShouldNotContain("Proposed");
        output.ShouldNotContain("Resolved");
        output.ShouldNotContain("Completed");
    }

    [Fact]
    public void StateCategories_DisplayInCorrectOrder()
    {
        var items = new[]
        {
            MakeItem(1, "Done task", WorkItemType.Task, state: "Closed"),
            MakeItem(2, "Active task", WorkItemType.Task, state: "Active"),
            MakeItem(3, "New task", WorkItemType.Task, state: "New"),
            MakeItem(4, "Resolved task", WorkItemType.Task, state: "Resolved"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        var proposedIdx = output.IndexOf("Proposed");
        var inProgressIdx = output.IndexOf("In Progress");
        var resolvedIdx = output.IndexOf("Resolved");
        var completedIdx = output.IndexOf("Completed");

        proposedIdx.ShouldBeLessThan(inProgressIdx);
        inProgressIdx.ShouldBeLessThan(resolvedIdx);
        resolvedIdx.ShouldBeLessThan(completedIdx);
    }

    [Fact]
    public void StateCategories_CountPerCategoryIsCorrect()
    {
        var items = new[]
        {
            MakeItem(1, "Task A", WorkItemType.Task, state: "New"),
            MakeItem(2, "Task B", WorkItemType.Task, state: "New"),
            MakeItem(3, "Task C", WorkItemType.Task, state: "Active"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);
        var plain = StripAnsi(output);

        // Proposed should show count 2
        plain.ShouldContain("Proposed (2)");
        // In Progress should show count 1
        plain.ShouldContain("In Progress (1)");
    }

    [Fact]
    public void StateCategories_EmptySprint_ShowsZeroItems()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());
        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("0 items");
        // No category headers should appear
        output.ShouldNotContain("Proposed");
        output.ShouldNotContain("In Progress");
    }

    [Fact]
    public void StateCategories_UnknownStates_FallToProposedBucket()
    {
        var items = new[]
        {
            MakeItem(1, "Custom state task", WorkItemType.Task, state: "SomeCustomState"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        // Unknown states should fall into the Proposed bucket
        output.ShouldContain("Proposed");
        output.ShouldContain("#1");
    }

    [Fact]
    public void StateCategories_WithCustomStateEntries_UsesProvidedCategories()
    {
        var stateEntries = new List<StateEntry>
        {
            new("Custom Active", StateCategory.InProgress, null),
            new("Custom Done", StateCategory.Completed, null),
        };
        var formatter = new HumanOutputFormatter(
            new Infrastructure.Config.DisplayConfig(),
            stateEntries: stateEntries);

        var items = new[]
        {
            MakeItem(1, "Active task", WorkItemType.Task, state: "Custom Active"),
            MakeItem(2, "Done task", WorkItemType.Task, state: "Custom Done"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("In Progress");
        output.ShouldContain("Completed");
        output.ShouldNotContain("Proposed");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 2: Process-hierarchy indentation for team view
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SprintView_WithHierarchy_IndentsChildrenUnderParents()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task2 = MakeItem(43, "Logout endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Parent is rendered as context node (dimmed)
        output.ShouldContain("Auth Feature");
        // Children use box-drawing characters
        output.ShouldContain("├── ");
        output.ShouldContain("└── ");
    }

    [Fact]
    public void SprintView_HierarchyWithCategoryGrouping_ShowsParentInEachCategory()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task2 = MakeItem(43, "Logout endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "New");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Parent appears in both Proposed (task2="New") and InProgress (task1="Active") groups
        output.ShouldContain("Proposed");
        output.ShouldContain("In Progress");
        output.ShouldContain("#42");
        output.ShouldContain("#43");
    }

    [Fact]
    public void SprintView_FilteredChildren_LastVisibleChildGetsClosingConnector()
    {
        // Regression test: when category filtering skips siblings, the last visible
        // child in a category must get └── (not ├──) to avoid a dangling vertical bar.
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var taskA = MakeItem(41, "Task A", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "New");       // Proposed
        var taskB = MakeItem(42, "Task B", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");    // InProgress
        var taskC = MakeItem(43, "Task C", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "New");       // Proposed

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { taskA, taskB, taskC }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { taskA, taskB, taskC }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);
        var plain = StripAnsi(output);

        // In the InProgress category, Task B is the only visible child — it should get └──
        var inProgressIdx = plain.IndexOf("In Progress");
        var resolvedIdx = plain.IndexOf("Resolved");
        if (resolvedIdx == -1) resolvedIdx = plain.Length;
        var inProgressSection = plain.Substring(inProgressIdx, resolvedIdx - inProgressIdx);
        inProgressSection.ShouldContain("└── ");
        inProgressSection.ShouldNotContain("├── ");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 3: Progress indicators per parent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ProgressIndicator_ShowsDoneOverTotal()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Closed");
        var task2 = MakeItem(43, "Logout", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task3 = MakeItem(44, "Profile", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Resolved");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2, task3 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2, task3 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Feature has 3 children: task1 Closed (Completed), task2 Active (InProgress), task3 Resolved
        // Done = Resolved + Completed = 2, Total = 3
        output.ShouldContain("[2/3]");
    }

    [Fact]
    public void ProgressIndicator_NotShownForLeafItems()
    {
        var task1 = MakeItem(42, "Solo task", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active");

        var hierarchy = SprintHierarchy.Build(new[] { task1 }, new Dictionary<int, WorkItem>(), new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Leaf items should not have progress indicators
        output.ShouldNotContain("[0/0]");
        output.ShouldNotContain("[/]"); // Spectre markup — not present in Human formatter (uses ANSI)
    }

    [Fact]
    public void ProgressIndicator_AllChildrenDone()
    {
        var feature = MakeItem(100, "Done Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Closed");
        var task2 = MakeItem(43, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Closed");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("[2/2]");
    }

    [Fact]
    public void ProgressIndicator_NoChildrenDone()
    {
        var feature = MakeItem(100, "Fresh Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "New");
        var task2 = MakeItem(43, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("[0/2]");
    }

    [Fact]
    public void ProgressIndicator_InWorkspaceView()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Task 1", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Closed");
        var task2 = MakeItem(43, "Task 2", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("[1/2]");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 4: Assignee column in team view
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SprintView_ShowsAssigneeInformation()
    {
        var items = new[]
        {
            MakeItem(1, "Task A", WorkItemType.Task, state: "Active", assignee: "Alice Smith"),
            MakeItem(2, "Task B", WorkItemType.Task, state: "Active", assignee: "Bob Jones"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        // Sprint view (team view) should show assignee info
        output.ShouldContain("@Alice Smith");
        output.ShouldContain("@Bob Jones");
    }

    [Fact]
    public void SprintView_ShowsUnassignedForNullAssignee()
    {
        var items = new[]
        {
            MakeItem(1, "Task A", WorkItemType.Task, state: "Active", assignee: null),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("@(unassigned)");
    }

    [Fact]
    public void SprintView_HierarchicalWithAssignee()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null, assignee: "Alice");
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Assignee info should appear with the items in team view
        output.ShouldContain("@Alice");
    }

    [Fact]
    public void WorkspaceView_DoesNotShowInlineAssignee()
    {
        // Personal workspace view should not show the @assignee inline suffix
        // (assignee is redundant — it's all the current user)
        var items = new[]
        {
            MakeItem(1, "Task A", WorkItemType.Task, state: "Active", assignee: "Alice Smith"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldNotContain("@Alice Smith");
    }

    // ═══════════════════════════════════════════════════════════════════
    // GroupByStateCategory helper tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GroupByStateCategory_ReturnsCorrectGroups()
    {
        var items = new[]
        {
            MakeItem(1, "New", WorkItemType.Task, state: "New"),
            MakeItem(2, "Active", WorkItemType.Task, state: "Active"),
            MakeItem(3, "Resolved", WorkItemType.Task, state: "Resolved"),
            MakeItem(4, "Closed", WorkItemType.Task, state: "Closed"),
            MakeItem(5, "Also New", WorkItemType.Task, state: "To Do"),
        };

        var groups = _formatter.GroupByStateCategory(items);

        groups.Count.ShouldBe(4);
        groups[0].Category.ShouldBe(StateCategory.Proposed);
        groups[0].Items.Count.ShouldBe(2); // "New" and "To Do"
        groups[1].Category.ShouldBe(StateCategory.InProgress);
        groups[1].Items.Count.ShouldBe(1); // "Active"
        groups[2].Category.ShouldBe(StateCategory.Resolved);
        groups[2].Items.Count.ShouldBe(1);
        groups[3].Category.ShouldBe(StateCategory.Completed);
        groups[3].Items.Count.ShouldBe(1); // "Closed"
    }

    [Fact]
    public void GroupByStateCategory_EmptyList_ReturnsEmpty()
    {
        var groups = _formatter.GroupByStateCategory(Array.Empty<WorkItem>());
        groups.Count.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FormatCategoryHeader tests
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(StateCategory.Proposed, "Proposed")]
    [InlineData(StateCategory.InProgress, "In Progress")]
    [InlineData(StateCategory.Resolved, "Resolved")]
    [InlineData(StateCategory.Completed, "Completed")]
    public void FormatCategoryHeader_ReturnsDisplayName(StateCategory category, string expected)
    {
        HumanOutputFormatter.FormatCategoryHeader(category).ShouldBe(expected);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FormatProgressIndicator tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FormatProgressIndicator_NoChildren_ReturnsEmpty()
    {
        var node = new SprintHierarchyNode(
            MakeItem(1, "Leaf", WorkItemType.Task, state: "Active"), isSprintItem: true);

        _formatter.FormatProgressIndicator(node).ShouldBe("");
    }

    [Fact]
    public void FormatProgressIndicator_WithChildren_ReturnsProgress()
    {
        var parent = new SprintHierarchyNode(
            MakeItem(100, "Parent", WorkItemType.Feature, state: "Active"), isSprintItem: false);
        var child1 = new SprintHierarchyNode(
            MakeItem(1, "Child 1", WorkItemType.Task, state: "Closed"), isSprintItem: true);
        var child2 = new SprintHierarchyNode(
            MakeItem(2, "Child 2", WorkItemType.Task, state: "Active"), isSprintItem: true);
        parent.Children.Add(child1);
        parent.Children.Add(child2);

        var result = _formatter.FormatProgressIndicator(parent);
        result.ShouldContain("[1/2]");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(
        int id,
        string title,
        WorkItemType type,
        string state = "New",
        int? parentId = null,
        string? assignee = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = state,
            ParentId = parentId,
            AssignedTo = assignee,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static string StripAnsi(string input)
        => Regex.Replace(input, "\u001b\\[[0-9;]*m", "");
}
