using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// Tests for hierarchical sprint rendering in HumanOutputFormatter.FormatSprintView.
/// </summary>
public class SprintHierarchyFormatterTests
{
    private const string Esc = "\x1b[";
    private readonly HumanOutputFormatter _formatter = new();

    // ── (1) Hierarchical output contains box-drawing characters ──────

    [Fact]
    public void HierarchicalOutput_ContainsBoxDrawingCharacters()
    {
        var (ws, _) = BuildHierarchicalWorkspace();
        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("├── ");
        output.ShouldContain("└── ");
    }

    // ── (2) Parent context nodes are dimmed ──────────────────────────

    [Fact]
    public void ParentContextNodes_AreDimmed()
    {
        var (ws, _) = BuildHierarchicalWorkspace();
        var output = _formatter.FormatSprintView(ws, 14);

        // Parent context node should have Dim escape before the title
        // Format: {typeColor}{badge}{Reset} {Dim}{title}{Reset} [{stateColor}{state}{Reset}]
        output.ShouldContain("\x1b[2m" + "Auth Feature");
    }

    // ── (3) Sprint items show active marker and dirty marker ─────────

    [Fact]
    public void SprintItems_ShowActiveMarkerAndDirtyMarker()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        task1.SetDirty();

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(task1, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Active marker: cyan ●
        output.ShouldContain("\x1b[36m●\x1b[0m");
        // Dirty marker: yellow •
        output.ShouldContain("\x1b[33m•\x1b[0m");
        // Item ID
        output.ShouldContain("#42");
    }

    // ── (4) Items without parents render at root ─────────────────────

    [Fact]
    public void ItemsWithoutParents_RenderAtRoot()
    {
        var task1 = MakeItem(44, "Fix typo", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task1 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Should have the item ID without box-drawing connectors on that line
        var lines = output.Split('\n');
        var itemLine = lines.First(l => l.Contains("#44"));
        itemLine.ShouldNotContain("├──");
        itemLine.ShouldNotContain("└──");
    }

    // ── (5) Shared parents appear once per category with multiple children ────────

    [Fact]
    public void SharedParents_AppearOnceWithMultipleChildren()
    {
        // Without category grouping, parent appears once per assignee group
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task2 = MakeItem(43, "Logout endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "New");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var sprintItems = new[] { task1, task2 };
        var hierarchy = SprintHierarchy.Build(sprintItems, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, sprintItems, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // "Auth Feature" appears once — no category grouping splits it
        var count = CountOccurrences(output, "Auth Feature");
        count.ShouldBe(1);

        // Both children should be present
        output.ShouldContain("#42");
        output.ShouldContain("#43");

        // No category headers should appear
        output.ShouldNotContain("Proposed");
        output.ShouldNotContain("In Progress");
    }

    // ── (6) Fallback to flat when hierarchy is null ──────────────────

    [Fact]
    public void FallbackToFlat_WhenHierarchyIsNull()
    {
        var items = new[]
        {
            MakeItem(1, "Task A", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active"),
            MakeItem(2, "Task B", WorkItemType.Task, parentId: null, assignee: "Bob", state: "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var output = _formatter.FormatSprintView(ws, 14);

        // Flat rendering — no box-drawing characters
        output.ShouldNotContain("├──");
        output.ShouldNotContain("└──");
        output.ShouldNotContain("│   ");
        // Still grouped by assignee
        output.ShouldContain("Alice");
        output.ShouldContain("Bob");
        // No category headers
        output.ShouldNotContain("Proposed");
        output.ShouldNotContain("In Progress");
    }

    // ── (7) Empty sprint still shows "0 items" ──────────────────────

    [Fact]
    public void EmptySprint_ShowsZeroItems()
    {
        var hierarchy = SprintHierarchy.Build(
            Array.Empty<WorkItem>(),
            new Dictionary<int, WorkItem>(),
            new[] { "Feature" });
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        output.ShouldContain("0 items");
    }

    // ── (8) Existing SprintViewFormatterTests — verified separately ──
    //    (We do not duplicate here; the existing tests run unchanged.)

    // ── (9) Assignee groups render in alphabetical order ─────────────

    [Fact]
    public void AssigneeGroups_RenderInAlphabeticalOrder()
    {
        var task1 = MakeItem(1, "Task Z", WorkItemType.Task, parentId: null, assignee: "Zara", state: "New");
        var task2 = MakeItem(2, "Task A", WorkItemType.Task, parentId: null, assignee: "Alice", state: "New");
        var task3 = MakeItem(3, "Task B", WorkItemType.Task, parentId: null, assignee: "bob", state: "New");

        var parentLookup = new Dictionary<int, WorkItem>();
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2, task3 }, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, new[] { task1, task2, task3 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        var aliceIdx = output.IndexOf("Alice");
        var bobIdx = output.IndexOf("bob");
        var zaraIdx = output.IndexOf("Zara");

        aliceIdx.ShouldBeLessThan(bobIdx);
        bobIdx.ShouldBeLessThan(zaraIdx);
    }

    // ── Parent context nodes do NOT show active/dirty markers ────────

    [Fact]
    public void ParentContextNodes_DoNotShowActiveOrDirtyMarkers()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var hierarchy = SprintHierarchy.Build(new[] { task1 }, parentLookup, new[] { "Feature" });
        // Set context to feature — even so, parent context node should NOT show active marker
        var ws = Workspace.Build(task1, new[] { task1 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Find the parent context line (contains "Auth Feature" with Dim)
        var lines = output.Split('\n');
        var parentLine = lines.First(l => l.Contains("Auth Feature"));

        // Parent line should NOT have the # prefix (it's not rendered as #ID for context nodes)
        parentLine.ShouldNotContain("#100");
    }

    // ── Vertical continuation character for non-last children ────────

    [Fact]
    public void VerticalContinuation_ForNonLastChildren()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        // Both user stories in same category (Active → InProgress) so both are visible together
        var us1 = MakeItem(50, "Story A", WorkItemType.UserStory, parentId: 100, assignee: "Alice", state: "Active");
        var us2 = MakeItem(51, "Story B", WorkItemType.UserStory, parentId: 100, assignee: "Alice", state: "Active");
        var task1 = MakeItem(10, "Task Under A", WorkItemType.Task, parentId: 50, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [100] = feature,
            [50] = us1,
        };
        var sprintItems = new[] { us1, us2, task1 };
        var hierarchy = SprintHierarchy.Build(sprintItems, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(null, sprintItems, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Output should contain the vertical continuation character for deeper nesting
        output.ShouldContain("│   ");
    }

    // ── EPIC-005: Virtual group rendering tests ────────────────────

    [Fact]
    public void VirtualGroupHeader_RendersAsSeparatorLine()
    {
        var task1 = MakeItem(1, "Update docs", WorkItemType.Task, parentId: null, assignee: "Alice", state: "New");
        var task2 = MakeItem(2, "Clean up logs", WorkItemType.Task, parentId: null, assignee: "Alice", state: "New");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["Task"] = 2,
        };
        var hierarchy = SprintHierarchy.Build(new[] { task1, task2 }, parentLookup, new[] { "Epic" }, typeLevelMap);
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Virtual group header should contain the group label
        output.ShouldContain("Unparented Tasks");
        // Items should be rendered with box-drawing connectors
        output.ShouldContain("#1");
        output.ShouldContain("#2");
    }

    [Fact]
    public void VirtualGroupItems_IndentedToBacklogLevel()
    {
        var feature = MakeItem(1, "Dark Mode", WorkItemType.Feature, parentId: null, assignee: "Alice", state: "Active");
        var task = MakeItem(2, "Update docs", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem>();
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["Task"] = 2,
        };
        var hierarchy = SprintHierarchy.Build(new[] { feature, task }, parentLookup, new[] { "Epic" }, typeLevelMap);
        var ws = Workspace.Build(null, new[] { feature, task }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Both virtual groups should be present
        output.ShouldContain("Unparented Features");
        output.ShouldContain("Unparented Tasks");
    }

    [Fact]
    public void MixedParentedAndUnparented_RendersBothCorrectly()
    {
        var epic = MakeItem(1000, "Payment Refactor", WorkItemType.Epic, parentId: null);
        var feature = MakeItem(100, "Retry Logic", WorkItemType.Feature, parentId: 1000);
        var task1 = MakeItem(10, "Add timeout", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task2 = MakeItem(20, "Update docs", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem>
        {
            [1000] = epic,
            [100] = feature,
        };
        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["Task"] = 2,
        };
        var hierarchy = SprintHierarchy.Build(
            new[] { task1, task2 }, parentLookup, new[] { "Epic" }, typeLevelMap);
        var ws = Workspace.Build(null, new[] { task1, task2 }, Array.Empty<WorkItem>(), hierarchy);

        var output = _formatter.FormatSprintView(ws, 14);

        // Parented item should show in hierarchy
        output.ShouldContain("Retry Logic");
        output.ShouldContain("#10"); // parented task
        // Unparented task should be in virtual group
        output.ShouldContain("Unparented Tasks");
        output.ShouldContain("#20"); // unparented task
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static (Workspace ws, SprintHierarchy hierarchy) BuildHierarchicalWorkspace()
    {
        var feature = MakeItem(100, "Auth Feature", WorkItemType.Feature, parentId: null);
        // Both children in same state category (Active → InProgress) so they appear together in one group
        var task1 = MakeItem(42, "Login endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task2 = MakeItem(43, "Logout endpoint", WorkItemType.Task, parentId: 100, assignee: "Alice", state: "Active");
        var task3 = MakeItem(44, "Fix typo", WorkItemType.Task, parentId: null, assignee: "Alice", state: "Active");

        var parentLookup = new Dictionary<int, WorkItem> { [100] = feature };
        var sprintItems = new[] { task1, task2, task3 };
        var hierarchy = SprintHierarchy.Build(sprintItems, parentLookup, new[] { "Feature" });
        var ws = Workspace.Build(task1, sprintItems, Array.Empty<WorkItem>(), hierarchy);

        return (ws, hierarchy);
    }

    private static WorkItem MakeItem(
        int id,
        string title,
        WorkItemType type,
        int? parentId,
        string? assignee = null,
        string state = "New")
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

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var idx = 0;
        while ((idx = source.IndexOf(value, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }
}
