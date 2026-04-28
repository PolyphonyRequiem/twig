using System.Text.RegularExpressions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.TestKit;
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
            new WorkItemBuilder(1, "New task").AsTask().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Active task").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(3, "Done task").AsTask().InState("Closed").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("Proposed");
        output.ShouldContain("In Progress");
        output.ShouldContain("Completed");
    }

    [Fact]
    public void SprintView_GroupsByAssignee_NotStateCategory()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "New task").AsTask().AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Active task").AsTask().InState("Active").AssignedTo("Bob").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(3, "Resolved task").AsTask().InState("Resolved").AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(4, "Done task").AsTask().InState("Closed").AssignedTo("Bob").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);
        var plain = StripAnsi(output);

        // Sprint view groups by assignee, not state category
        output.ShouldContain("Alice");
        output.ShouldContain("Bob");
        // No state category headers (e.g. "Proposed (1)", "In Progress (1)")
        plain.ShouldNotContain("Proposed (");
        plain.ShouldNotContain("In Progress (");
        plain.ShouldNotContain("Resolved (");
        plain.ShouldNotContain("Completed (");
    }

    [Fact]
    public void StateCategories_EmptyCategoriesAreOmitted()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "Active task").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Another active").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatWorkspace(ws, 14);
        var plain = StripAnsi(output);

        output.ShouldContain("In Progress");
        // Category headers for empty categories should not appear;
        // check for header pattern "(N)" to distinguish from progress footer text
        plain.ShouldNotContain("Proposed (");
        plain.ShouldNotContain("Resolved (");
        plain.ShouldNotContain("Completed (");
    }

    [Fact]
    public void StateCategories_DisplayInCorrectOrder()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "Done task").AsTask().InState("Closed").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Active task").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(3, "New task").AsTask().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(4, "Resolved task").AsTask().InState("Resolved").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
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
            new WorkItemBuilder(1, "Task A").AsTask().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Task B").AsTask().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(3, "Task C").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
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
            new WorkItemBuilder(1, "Custom state task").AsTask().InState("SomeCustomState").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
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
            new WorkItemBuilder(1, "Active task").AsTask().InState("Custom Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Done task").AsTask().InState("Custom Done").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = formatter.FormatWorkspace(ws, 14);
        var plain = StripAnsi(output);

        output.ShouldContain("In Progress");
        output.ShouldContain("Completed");
        // Check for category header pattern to avoid matching progress footer "proposed"
        plain.ShouldNotContain("Proposed (");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 2: Process-hierarchy indentation for team view
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SprintView_WithHierarchy_IndentsChildrenUnderParents()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Login endpoint").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Logout endpoint").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Parent is rendered as context node (dimmed)
        output.ShouldContain("Auth Feature");
        // Children use box-drawing characters
        output.ShouldContain("├── ");
        output.ShouldContain("└── ");
    }

    [Fact]
    public void SprintView_HierarchyWithoutCategoryGrouping_ShowsParentOnce()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Login endpoint").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Logout endpoint").AsTask().WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // No category grouping — parent appears once under assignee
        output.ShouldNotContain("Proposed (");
        output.ShouldNotContain("In Progress (");
        output.ShouldContain("#42");
        output.ShouldContain("#43");
    }

    [Fact]
    public void SprintView_AllChildren_LastChildGetsClosingConnector()
    {
        // Without category filtering, all children are visible.
        // The last child must get └── (not ├──).
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var taskA = new WorkItemBuilder(41, "Task A").AsTask().WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var taskB = new WorkItemBuilder(42, "Task B").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var taskC = new WorkItemBuilder(43, "Task C").AsTask().WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { taskA, taskB, taskC }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { taskA, taskB, taskC }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);
        var plain = StripAnsi(output);

        // All 3 children are visible under the single assignee group.
        // Task C (last) should get └──, others ├──
        plain.ShouldContain("├── ");
        plain.ShouldContain("└── ");
        // No category headers
        plain.ShouldNotContain("Proposed (");
        plain.ShouldNotContain("In Progress (");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 3: Progress indicators per parent
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ProgressIndicator_ShowsDoneOverTotal()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Login").AsTask().InState("Closed").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Logout").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task3 = new WorkItemBuilder(44, "Profile").AsTask().InState("Resolved").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2, task3 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2, task3 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Feature has 3 children: task1 Closed (Completed), task2 Active (InProgress), task3 Resolved
        // Done = Resolved + Completed = 2, Total = 3
        output.ShouldContain("[2/3]");
    }

    [Fact]
    public void ProgressIndicator_NotShownForLeafItems()
    {
        var task1 = new WorkItemBuilder(42, "Solo task").AsTask().InState("Active").AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1 }, new Dictionary<int, WorkItem>(), new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Leaf items should not have progress indicators
        output.ShouldNotContain("[0/0]");
        output.ShouldNotContain("[/]"); // Spectre markup — not present in Human formatter (uses ANSI)
    }

    [Fact]
    public void ProgressIndicator_AllChildrenDone()
    {
        var feature = new WorkItemBuilder(100, "Done Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Task 1").AsTask().InState("Closed").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Task 2").AsTask().InState("Closed").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("[2/2]");
    }

    [Fact]
    public void ProgressIndicator_NoChildrenDone()
    {
        var feature = new WorkItemBuilder(100, "Fresh Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Task 1").AsTask().WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Task 2").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("[0/2]");
    }

    [Fact]
    public void ProgressIndicator_InWorkspaceView()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Task 1").AsTask().InState("Closed").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task2 = new WorkItemBuilder(43, "Task 2").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1, task2 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatWorkspace(ws, 14);

        output.ShouldContain("[1/2]");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Task 4: Assignee column in team view
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void SprintView_ShowsAssigneeAsGroupHeader()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "Task A").AsTask().InState("Active").AssignedTo("Alice Smith").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Task B").AsTask().InState("Active").AssignedTo("Bob Jones").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        // Sprint view groups by assignee — assignee appears as group header
        output.ShouldContain("Alice Smith");
        output.ShouldContain("Bob Jones");
    }

    [Fact]
    public void SprintView_ShowsUnassignedGroupHeader()
    {
        var items = new[]
        {
            new WorkItemBuilder(1, "Task A").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        // Unassigned items appear under "(unassigned)" group header
        output.ShouldContain("(unassigned)");
    }

    [Fact]
    public void SprintView_HierarchicalAssigneeAsGroupHeader()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();
        var task1 = new WorkItemBuilder(42, "Login endpoint").AsTask().InState("Active").WithParent(100).AssignedTo("Alice").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = new SprintHierarchyBuilder().Build(new[] { task1 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Assignee shown as group header in hierarchical sprint view
        output.ShouldContain("Alice");
    }

    [Fact]
    public void WorkspaceView_DoesNotShowInlineAssignee()
    {
        // Personal workspace view should not show the @assignee inline suffix
        // (assignee is redundant — it's all the current user)
        var items = new[]
        {
            new WorkItemBuilder(1, "Task A").AsTask().InState("Active").AssignedTo("Alice Smith").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
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
            new WorkItemBuilder(1, "New").AsTask().WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(2, "Active").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(3, "Resolved").AsTask().InState("Resolved").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(4, "Closed").AsTask().InState("Closed").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
            new WorkItemBuilder(5, "Also New").AsTask().InState("To Do").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(),
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
            new WorkItemBuilder(1, "Leaf").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(), isSprintItem: true);

        _formatter.FormatProgressIndicator(node).ShouldBe("");
    }

    [Fact]
    public void FormatProgressIndicator_WithChildren_ReturnsProgress()
    {
        var parent = new SprintHierarchyNode(
            new WorkItemBuilder(100, "Parent").AsFeature().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(), isSprintItem: false);
        var child1 = new SprintHierarchyNode(
            new WorkItemBuilder(1, "Child 1").AsTask().InState("Closed").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(), isSprintItem: true);
        var child2 = new SprintHierarchyNode(
            new WorkItemBuilder(2, "Child 2").AsTask().InState("Active").WithIterationPath(@"Project\Sprint 1").WithAreaPath("Project").Build(), isSprintItem: true);
        parent.Children.Add(child1);
        parent.Children.Add(child2);

        var result = _formatter.FormatProgressIndicator(parent);
        result.ShouldContain("[1/2]");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string StripAnsi(string input)
        => Regex.Replace(input, "\u001b\\[[0-9;]*m", "");
}
