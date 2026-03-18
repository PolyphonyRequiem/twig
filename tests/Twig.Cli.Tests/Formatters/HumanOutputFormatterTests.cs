using System.Text.RegularExpressions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class HumanOutputFormatterTests
{
    private readonly HumanOutputFormatter _formatter = new();

    // ── WorkItem formatting ─────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_IncludesAnsiEscapeCodes()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b["); // ANSI escape present
    }

    [Fact]
    public void FormatWorkItem_ShowsBoldTitle()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b[1m"); // Bold
        result.ShouldContain("#123");
        result.ShouldContain("My Task");
    }

    [Fact]
    public void FormatWorkItem_ShowsDirtyMarker_WhenDirty()
    {
        var item = CreateWorkItem(123, "Dirty Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: true);

        result.ShouldContain("•");
    }

    [Fact]
    public void FormatWorkItem_HidesDirtyMarker_WhenShowDirtyFalse()
    {
        var item = CreateWorkItem(123, "Dirty Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("•");
    }

    [Fact]
    public void FormatWorkItem_ShowsStateWithColor()
    {
        var item = CreateWorkItem(123, "My Task", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("Active");
        result.ShouldContain("\x1b[34m"); // Blue for active (InProgress)
    }

    [Fact]
    public void FormatWorkItem_ShowsAllFields()
    {
        var item = CreateWorkItem(123, "My Task", "Active", assignedTo: "dangreen");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("Type:");
        result.ShouldContain("State:");
        result.ShouldContain("Assigned:");
        result.ShouldContain("dangreen");
        result.ShouldContain("Area:");
        result.ShouldContain("Iteration:");
    }

    [Fact]
    public void FormatWorkItem_ShowsUnassigned_WhenNull()
    {
        var item = CreateWorkItem(123, "My Task", "New");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("(unassigned)");
    }

    // ── Tree formatting ─────────────────────────────────────────────

    [Fact]
    public void FormatTree_ShowsBoxDrawingCharacters()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child1 = CreateWorkItem(2, "Child 1", "New");
        var child2 = CreateWorkItem(3, "Child 2", "Done");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("├── ");
        result.ShouldContain("└── ");
    }

    [Fact]
    public void FormatTree_ShowsActiveMarker()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: 1);

        result.ShouldContain("●");
    }

    [Fact]
    public void FormatTree_ShowsStateName()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("["); // state in brackets
        result.ShouldContain("Active");
    }

    [Fact]
    public void FormatTree_ShowsDirtyMarker()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        focus.UpdateField("test", "val");
        focus.ApplyCommands();
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("•");
    }

    [Fact]
    public void FormatTree_ShowsParentChain()
    {
        var parent = CreateWorkItem(1, "Parent", "Active");
        var focus = CreateWorkItem(2, "Focus", "New");
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("Parent");
    }

    [Fact]
    public void FormatTree_CollapsesExcessChildren()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var children = new WorkItem[]
        {
            CreateWorkItem(2, "Child 1", "New"),
            CreateWorkItem(3, "Child 2", "New"),
            CreateWorkItem(4, "Child 3", "New"),
            CreateWorkItem(5, "Child 4", "New"),
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), children);

        var result = _formatter.FormatTree(tree, maxChildren: 2, activeId: null);

        result.ShouldContain("... and 2 more");
    }

    // ── Workspace formatting ────────────────────────────────────────

    [Fact]
    public void FormatWorkspace_ShowsSections()
    {
        var ctx = CreateWorkItem(1, "Active Item", "Active");
        var sprintItems = new[] { ctx, CreateWorkItem(2, "Other", "New") };
        var ws = Workspace.Build(ctx, sprintItems, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("Workspace");
        result.ShouldContain("Active:");
        result.ShouldContain("Sprint");
        result.ShouldContain("#1");
        result.ShouldContain("#2");
    }

    [Fact]
    public void FormatWorkspace_ShowsNoContext()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("(none)");
    }

    [Fact]
    public void FormatWorkspace_ShowsSeeds()
    {
        var seed = CreateSeed(-1, "Seed Task");
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("Seeds");
        result.ShouldContain("Seed Task");
    }

    [Fact]
    public void FormatWorkspace_ShowsStaleSeedWarning()
    {
        var staleSeed = new WorkItem
        {
            Id = -2,
            Type = WorkItemType.Task,
            Title = "Stale Seed",
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { staleSeed });

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("stale");
    }

    [Fact]
    public void FormatWorkspace_ShowsDirtyCount()
    {
        var dirty = CreateWorkItem(1, "Dirty Item", "Active");
        dirty.UpdateField("test", "val");
        dirty.ApplyCommands();
        var ws = Workspace.Build(null, new[] { dirty }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("unsaved changes");
    }

    // ── Disambiguation ──────────────────────────────────────────────

    [Fact]
    public void FormatDisambiguation_ShowsNumberedList()
    {
        var matches = new List<(int Id, string Title)>
        {
            (12345, "Fix login bug"),
            (12346, "Fix logout bug"),
        };

        var result = _formatter.FormatDisambiguation(matches);

        result.ShouldContain("[1]");
        result.ShouldContain("[2]");
        result.ShouldContain("#12345");
        result.ShouldContain("#12346");
        result.ShouldContain("Fix login bug");
    }

    // ── FieldChange ─────────────────────────────────────────────────

    [Fact]
    public void FormatFieldChange_ShowsFieldNameAndValues()
    {
        var change = new FieldChange("System.Title", "Old Title", "New Title");

        var result = _formatter.FormatFieldChange(change);

        result.ShouldContain("System.Title");
        result.ShouldContain("Old Title");
        result.ShouldContain("New Title");
    }

    [Fact]
    public void FormatFieldChange_HandlesNullValues()
    {
        var change = new FieldChange("System.AssignedTo", null, "dangreen");

        var result = _formatter.FormatFieldChange(change);

        result.ShouldContain("(empty)");
        result.ShouldContain("dangreen");
    }

    // ── Error/Success ───────────────────────────────────────────────

    [Fact]
    public void FormatError_ContainsAnsiRed()
    {
        var result = _formatter.FormatError("something went wrong");

        result.ShouldContain("\x1b[31m"); // Red
        result.ShouldContain("something went wrong");
    }

    [Fact]
    public void FormatSuccess_ContainsAnsiGreen()
    {
        var result = _formatter.FormatSuccess("all good");

        result.ShouldContain("\x1b[32m"); // Green
        result.ShouldContain("all good");
    }

    // ── WorkItem Type Color ─────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ShowsTypeColor_ForEpic()
    {
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Epic,
            Title = "Epic Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        var expectedColor = DeterministicTypeColor.GetAnsiEscape("Epic");
        result.ShouldContain(expectedColor); // Deterministic color for Epic type
        result.ShouldContain("Epic");
        result.ShouldContain("◆"); // Epic badge
    }

    [Fact]
    public void FormatWorkItem_ShowsTypeColor_ForFeature()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Feature,
            Title = "Feature Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(DeterministicTypeColor.GetAnsiEscape("Feature")); // Deterministic color for Feature type
    }

    [Fact]
    public void FormatWorkItem_ShowsTypeColor_ForUserStory()
    {
        var item = new WorkItem
        {
            Id = 2,
            Type = WorkItemType.UserStory,
            Title = "User Story Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(DeterministicTypeColor.GetAnsiEscape("User Story")); // Deterministic color for User Story type
    }

    [Fact]
    public void FormatWorkItem_ShowsTypeColor_ForBug()
    {
        var item = new WorkItem
        {
            Id = 3,
            Type = WorkItemType.Bug,
            Title = "Bug Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(DeterministicTypeColor.GetAnsiEscape("Bug")); // Deterministic color for Bug type
    }

    [Fact]
    public void FormatWorkItem_ShowsTypeColor_ForTask_UsesReset()
    {
        var item = new WorkItem
        {
            Id = 4,
            Type = WorkItemType.Task,
            Title = "Task Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        // Task uses deterministic hash color
        var expectedTaskColor = DeterministicTypeColor.GetAnsiEscape("Task");
        result.ShouldContain($"{expectedTaskColor}□ {WorkItemType.Task}\x1b[0m");
    }

    // ── FormatHint / FormatInfo ─────────────────────────────────────

    [Fact]
    public void FormatHint_ReturnsDimHintPrefix()
    {
        var result = _formatter.FormatHint("test hint");

        result.ShouldContain("\x1b[2m");  // Dim
        result.ShouldContain("hint:");
        result.ShouldContain("test hint");
        result.ShouldContain("\x1b[0m");  // Reset
    }

    [Fact]
    public void FormatInfo_ReturnsDimMessage()
    {
        var result = _formatter.FormatInfo("loading items...");

        result.ShouldContain("\x1b[2m");  // Dim
        result.ShouldContain("loading items...");
        result.ShouldContain("\x1b[0m");  // Reset
    }

    // ── Type Badges ───────────────────────────────────────────────

    [Fact]
    public void FormatWorkItem_ShowsBadge_ForBug()
    {
        var item = new WorkItem
        {
            Id = 10,
            Type = WorkItemType.Bug,
            Title = "Bug Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("✦");
    }

    [Fact]
    public void FormatWorkItem_UsesTrueColor_WhenTypeColorsConfigured()
    {
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = new Dictionary<string, string> { ["Epic"] = "FF7B00" } });
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Epic,
            Title = "Epic Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b[38;2;255;123;0m");
    }

    [Fact]
    public void FormatWorkItem_FallsBackTo3BitColor_WhenNoTypeColors()
    {
        var formatter = new HumanOutputFormatter();
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Epic,
            Title = "Epic Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(DeterministicTypeColor.GetAnsiEscape("Epic")); // Deterministic fallback
    }

    [Fact]
    public void FormatWorkItem_UsesTrueColor_WhenTypeAppearancesProvided()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Epic", Color = "#FF00FF" },
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig(), appearances);
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Epic,
            Title = "Epic Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        // #FF00FF → RGB(255, 0, 255)
        result.ShouldContain("\x1b[38;2;255;0;255m");
    }

    [Fact]
    public void FormatWorkItem_UsesTrueColor_CaseInsensitiveKey()
    {
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = new Dictionary<string, string> { ["epic"] = "FF7B00" } });
        var item = new WorkItem
        {
            Id = 42,
            Type = WorkItemType.Epic, // Value = "Epic"
            Title = "Epic Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b[38;2;255;123;0m");
    }

    [Fact]
    public void FormatWorkItem_ShowsBadge_ForFeature()
    {
        var item = new WorkItem
        {
            Id = 11,
            Type = WorkItemType.Feature,
            Title = "Feature Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("▪");
    }

    [Fact]
    public void FormatTree_ShowsTypeBadges()
    {
        var focus = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Epic,
            Title = "Epic Focus",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var child1 = CreateWorkItem(2, "Task Child 1", "New");
        var child2 = CreateWorkItem(3, "Task Child 2", "Done");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain("◆"); // Epic badge
        result.ShouldContain("□"); // Task badge
    }

    [Fact]
    public void FormatWorkspace_ShowsTypeBadges()
    {
        var bug = new WorkItem
        {
            Id = 5,
            Type = WorkItemType.Bug,
            Title = "Bug Sprint Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var ws = Workspace.Build(null, new[] { bug }, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        result.ShouldContain("✦"); // Bug badge
    }

    // ── Integration: TypeColors + Badges across all format methods ──

    [Fact]
    public void Integration_FormatWorkItem_WithTypeColors_ShowsTrueColorAndBadge()
    {
        var typeColors = new Dictionary<string, string>
        {
            ["Epic"] = "FF7B00",
            ["Bug"] = "CC0000",
            ["Feature"] = "00AAFF",
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors });
        var item = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Epic,
            Title = "Render colors",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        // True-color ANSI for FF7B00 (R=255, G=123, B=0)
        result.ShouldContain("\x1b[38;2;255;123;0m");
        // Epic badge
        result.ShouldContain("◆");
        // Both badge and type name on the same Type line
        result.ShouldContain("◆ Epic");
    }

    [Fact]
    public void Integration_FormatTree_WithTypeColors_ShowsTrueColorAndBadge()
    {
        var typeColors = new Dictionary<string, string>
        {
            ["Epic"] = "FF7B00",
            ["Bug"] = "CC0000",
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors });
        var focus = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Epic,
            Title = "Epic Focus",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var child = new WorkItem
        {
            Id = 2,
            Type = WorkItemType.Bug,
            Title = "Bug Child",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Epic true-color + badge on focused item
        result.ShouldContain("\x1b[38;2;255;123;0m");
        result.ShouldContain("◆");
        // Bug true-color + badge on child
        result.ShouldContain("\x1b[38;2;204;0;0m");
        result.ShouldContain("✦");
    }

    [Fact]
    public void Integration_FormatWorkspace_WithTypeColors_ShowsTrueColorAndBadge()
    {
        var typeColors = new Dictionary<string, string>
        {
            ["Feature"] = "00AAFF",
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors });
        var feature = new WorkItem
        {
            Id = 7,
            Type = WorkItemType.Feature,
            Title = "Feature Sprint Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var ws = Workspace.Build(feature, new[] { feature }, Array.Empty<WorkItem>());

        var result = formatter.FormatWorkspace(ws, staleDays: 14);

        // True-color ANSI for 00AAFF (R=0, G=170, B=255)
        result.ShouldContain("\x1b[38;2;0;170;255m");
        // Feature badge
        result.ShouldContain("▪");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, string state, string? assignedTo = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = state,
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateSeed(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    // ── EPIC-014: Tree indentation alignment ────────────────────────

    [Fact]
    public void FormatTree_ParentChainIndentsPerDepth()
    {
        var grandparent = new WorkItem
        {
            Id = 1, Type = WorkItemType.Epic, Title = "Epic", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var parent = new WorkItem
        {
            Id = 2, Type = WorkItemType.Feature, Title = "Feature", State = "Active",
            ParentId = 1,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var focus = CreateWorkItem(3, "Focus Task", "Active");
        var tree = WorkTree.Build(focus, new[] { grandparent, parent }, Array.Empty<WorkItem>());

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // All items present in tree output
        result.ShouldContain("Epic");
        result.ShouldContain("Feature");
        result.ShouldContain("#3");
        result.ShouldContain("Focus Task");

        var lines = result.Replace("\r\n", "\n").Split('\n');
        lines.Length.ShouldBe(3);

        // Verify structure: grandparent on line 0, parent on line 1, focus on line 2
        lines[0].ShouldContain("Epic");
        lines[1].ShouldContain("Feature");
        lines[2].ShouldContain("#3");

        // Active marker on the focused item line
        lines[2].ShouldContain("●");
    }

    [Fact]
    public void FormatTree_ChildrenIndentUnderFocus()
    {
        var parent = new WorkItem
        {
            Id = 1, Type = WorkItemType.Epic, Title = "Epic Parent", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var focus = CreateWorkItem(2, "Focus", "Active");
        var child1 = CreateWorkItem(3, "Child 1", "New");
        var child2 = CreateWorkItem(4, "Child 2", "Done");
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child1, child2 });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var lines = result.Replace("\r\n", "\n").Split('\n');

        // Parent at depth 0, focus at depth 1, children at depth 2
        lines.Length.ShouldBe(4);

        // Children lines should contain box-drawing
        lines[2].ShouldContain("├── ");
        lines[3].ShouldContain("└── ");

        // Children should be indented more than the focus
        var focusIndent = StripAnsi(lines[1]).Length - StripAnsi(lines[1]).TrimStart().Length;
        var child1Indent = StripAnsi(lines[2]).Length - StripAnsi(lines[2]).TrimStart().Length;
        child1Indent.ShouldBeGreaterThan(focusIndent);
    }

    [Fact]
    public void FormatTree_FocusWithNoParents_AtZeroIndent()
    {
        var focus = CreateWorkItem(1, "Root Focus", "Active");
        var child = CreateWorkItem(2, "Child", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);
        var lines = result.Replace("\r\n", "\n").Split('\n');

        // Focus at depth 0 — starts at column 0 with active marker
        StripAnsi(lines[0]).ShouldStartWith("●");
        // Child at depth 1 — 2-space indent + box-drawing
        StripAnsi(lines[1]).ShouldStartWith("  └── ");
    }

    // ── EPIC-014: Type color bridging ───────────────────────────────

    [Fact]
    public void FormatTree_WithTypeColors_UsesTrueColorInTree()
    {
        var typeColors = new Dictionary<string, string>
        {
            ["Epic"] = "FF7B00",
            ["Task"] = "F2CB1D",
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors });

        var parent = new WorkItem
        {
            Id = 1, Type = WorkItemType.Epic, Title = "Epic Parent", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var focus = CreateWorkItem(2, "Task Focus", "Active");
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>());

        var result = formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Focus uses Task true-color
        result.ShouldContain("\x1b[38;2;242;203;29m");
    }

    // ── EPIC-014: Badge rendering ───────────────────────────────────

    [Fact]
    public void FormatTree_BadgesRenderForAllTypes()
    {
        var focus = new WorkItem
        {
            Id = 1, Type = WorkItemType.Epic, Title = "Epic", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var bugChild = new WorkItem
        {
            Id = 2, Type = WorkItemType.Bug, Title = "Bug", State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var taskChild = CreateWorkItem(3, "Task", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { bugChild, taskChild });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Verify badge characters are present for all types
        var stripped = StripAnsi(result);
        stripped.ShouldContain("◆"); // Epic badge
        stripped.ShouldContain("✦"); // Bug badge
        stripped.ShouldContain("□"); // Task badge
    }

    private static string StripAnsi(string input)
        => Regex.Replace(input, "\u001b\\[[0-9;]*m", "");

    // ── EPIC-3: Custom type badge and color ──────────────────────────

    [Theory]
    [InlineData("Scenario", "S")]
    [InlineData("Deliverable", "D")]
    [InlineData("Initiative", "I")]
    [InlineData("Custom Bug", "C")]
    public void FormatWorkItem_CustomType_BadgeIsFirstLetterUppercased(string typeName, string expectedBadge)
    {
        var type = WorkItemType.Parse(typeName).Value;
        var item = new WorkItem
        {
            Id = 99,
            Type = type,
            Title = "Custom Type Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(expectedBadge);
    }

    [Fact]
    public void FormatWorkItem_CustomType_WithTypeColors_UsesConfiguredColor()
    {
        var typeColors = new Dictionary<string, string> { ["Scenario"] = "009CCC" };
        var formatter = new HumanOutputFormatter(new DisplayConfig { TypeColors = typeColors });
        var type = WorkItemType.Parse("Scenario").Value;
        var item = new WorkItem
        {
            Id = 42,
            Type = type,
            Title = "Scenario Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        // True-color ANSI for 009CCC (R=0, G=156, B=204)
        result.ShouldContain("\x1b[38;2;0;156;204m");
    }

    [Fact]
    public void FormatWorkItem_CustomType_NoConfig_ReturnsDeterministicAnsiColor()
    {
        var formatter = new HumanOutputFormatter();
        var type = WorkItemType.Parse("Scenario").Value;
        var item = new WorkItem
        {
            Id = 42,
            Type = type,
            Title = "Scenario Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        // Should contain some ANSI color (not just reset) — deterministic hash-based
        result.ShouldContain("\x1b[");
    }

    [Fact]
    public void FormatWorkItem_SameCustomTypeName_AlwaysSameColor()
    {
        var formatter = new HumanOutputFormatter();
        var type = WorkItemType.Parse("Scenario").Value;
        var item = new WorkItem
        {
            Id = 1, Type = type, Title = "Item", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        // Calling twice should produce identical output (deterministic)
        var result1 = formatter.FormatWorkItem(item, showDirty: false);
        var result2 = formatter.FormatWorkItem(item, showDirty: false);

        result1.ShouldBe(result2);
    }

    [Fact]
    public void FormatWorkItem_EmptyTypeName_UsesFallbackSquareBadge()
    {
        // The '■' fallback in GetTypeBadge() is reached when Value is an empty string.
        // WorkItemType.Parse() rejects empty strings, so we use reflection to construct
        // a WorkItemType with Value="" solely for branch coverage of the fallback path.
        var ctor = typeof(WorkItemType)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == typeof(string));
        var type = (WorkItemType)ctor.Invoke(new object[] { string.Empty });

        var item = new WorkItem
        {
            Id = 7,
            Type = type,
            Title = "Fallback Badge Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("■");
    }

    // ── EPIC-3: GetStateColor via StateCategoryResolver ──────────────

    [Fact]
    public void FormatWorkItem_UnknownCustomState_UsesResetColor()
    {
        // Custom/unknown state names (e.g. "Draft", "Review") should fall through to Reset
        var item = CreateWorkItem(100, "Custom State Item", "Draft");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b[0m"); // Reset color for unknown state
        result.ShouldContain("Draft");
    }

    [Fact]
    public void FormatWorkItem_EmptyState_UsesDimColor()
    {
        var item = CreateWorkItem(101, "No State Item", "");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\x1b[2m"); // Dim color for empty state
    }

    [Theory]
    [InlineData("Closed", "\x1b[32m")]   // Green — Completed
    [InlineData("Done", "\x1b[32m")]     // Green — Completed
    [InlineData("Resolved", "\x1b[32m")] // Green — Resolved
    [InlineData("Active", "\x1b[34m")]   // Blue  — InProgress
    [InlineData("New", "\x1b[2m")]       // Dim   — Proposed
    [InlineData("Removed", "\x1b[31m")]  // Red   — Removed
    public void FormatWorkItem_StateColor_DelegatesViaStateCategoryResolver(string state, string expectedAnsi)
    {
        var item = CreateWorkItem(102, $"State={state}", state);

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(expectedAnsi);
    }

    // ── EPIC-002: iconId-based badge resolution ─────────────────────

    [Fact]
    public void GetTypeBadge_CustomType_WithIconId_ReturnsIconIdGlyph()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Deliverable", Color = "gold", IconId = "icon_trophy" },
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { Icons = "unicode" }, appearances);
        var item = new WorkItem
        {
            Id = 200,
            Type = WorkItemType.Parse("Deliverable").Value,
            Title = "Deliverable Item",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("★"); // icon_trophy → ★ in unicode mode
    }

    [Fact]
    public void GetTypeBadge_CustomType_WithIconId_Nerd_ReturnsNerdGlyph()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Deliverable", Color = "gold", IconId = "icon_trophy" },
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { Icons = "nerd" }, appearances);
        var item = new WorkItem
        {
            Id = 201,
            Type = WorkItemType.Parse("Deliverable").Value,
            Title = "Deliverable Nerd",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("\uEB20"); // icon_trophy → nf-cod-milestone in nerd mode (+ trailing space from NormalizeBadgeWidth)
    }

    [Fact]
    public void GetTypeBadge_StandardType_WithIconId_ReturnsIconIdGlyph()
    {
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Epic", Color = "purple", IconId = "icon_crown" },
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { Icons = "unicode" }, appearances);
        var item = new WorkItem
        {
            Id = 202,
            Type = WorkItemType.Epic,
            Title = "Epic With IconId",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("◆"); // icon_crown → ◆ (same as hardcoded Epic badge)
    }

    [Fact]
    public void GetTypeBadge_CustomType_NoIconId_FallsBackToFirstLetter()
    {
        // No typeAppearances at all — custom type falls back to first letter
        var formatter = new HumanOutputFormatter();
        var item = new WorkItem
        {
            Id = 203,
            Type = WorkItemType.Parse("Deliverable").Value,
            Title = "Deliverable No IconId",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("D"); // first letter fallback
    }

    [Fact]
    public void GetTypeBadge_CustomType_WithAppearances_ButNoIconId_FallsBackToFirstLetter()
    {
        // TypeAppearances present, but this type has no iconId — falls back to first letter
        var appearances = new List<TypeAppearanceConfig>
        {
            new() { Name = "Deliverable", Color = "gold", IconId = null },
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig { Icons = "unicode" }, appearances);
        var item = new WorkItem
        {
            Id = 204,
            Type = WorkItemType.Parse("Deliverable").Value,
            Title = "Deliverable Null IconId",
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("D"); // first letter fallback — iconId is null
    }
}
