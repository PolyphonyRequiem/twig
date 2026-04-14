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

        result.ShouldContain("✎");
    }

    [Fact]
    public void FormatWorkItem_HidesDirtyMarker_WhenShowDirtyFalse()
    {
        var item = CreateWorkItem(123, "Dirty Task", "Active");
        item.UpdateField("test", "value");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("✎");
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

        result.ShouldContain("✎");
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
        result.ShouldContain("✗ error:");
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

        result.ShouldContain("\x1b[33m→\x1b[0m");  // Yellow arrow prefix
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

    // ── EPIC-005: Tree view unparented banner ──────────────────────

    [Fact]
    public void FormatTree_UnparentedNonRootItem_ShowsBanner()
    {
        var focus = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Orphan Task",
            State = "Active",
            ParentId = null, // no parent
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["User Story"] = 2, ["Task"] = 3,
        };
        var parentChildMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Feature" },
            ["Feature"] = new List<string> { "User Story" },
            ["User Story"] = new List<string> { "Task" },
        };

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null, typeLevelMap, parentChildMap);

        result.ShouldContain("unparented");
        result.ShouldContain("expected under a User Story");
    }

    [Fact]
    public void FormatTree_UnparentedRootItem_NoBanner()
    {
        var focus = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Epic,
            Title = "Root Epic",
            State = "Active",
            ParentId = null,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["Task"] = 2,
        };
        var parentChildMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Feature" },
            ["Feature"] = new List<string> { "Task" },
        };

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null, typeLevelMap, parentChildMap);

        result.ShouldNotContain("unparented");
    }

    [Fact]
    public void FormatTree_ItemWithParent_NoBanner()
    {
        var parent = new WorkItem
        {
            Id = 100,
            Type = WorkItemType.Feature,
            Title = "Parent Feature",
            State = "Active",
            ParentId = null,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var focus = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Child Task",
            State = "Active",
            ParentId = 100,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>());

        var typeLevelMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0, ["Feature"] = 1, ["Task"] = 2,
        };
        var parentChildMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Feature" },
            ["Feature"] = new List<string> { "Task" },
        };

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null, typeLevelMap, parentChildMap);

        result.ShouldNotContain("unparented");
    }

    [Fact]
    public void FindExpectedParentTypeName_FindsParentType()
    {
        var parentChildMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Feature" },
            ["Feature"] = new List<string> { "Task" },
        };

        HumanOutputFormatter.FindExpectedParentTypeName("Task", parentChildMap).ShouldBe("Feature");
        HumanOutputFormatter.FindExpectedParentTypeName("Feature", parentChildMap).ShouldBe("Epic");
        HumanOutputFormatter.FindExpectedParentTypeName("Epic", parentChildMap).ShouldBeNull();
    }

    [Fact]
    public void FindExpectedParentTypeName_CaseInsensitiveLookup()
    {
        var parentChildMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Feature" },
            ["Feature"] = new List<string> { "Task" },
        };

        HumanOutputFormatter.FindExpectedParentTypeName("task", parentChildMap).ShouldBe("Feature");
        HumanOutputFormatter.FindExpectedParentTypeName("FEATURE", parentChildMap).ShouldBe("Epic");
    }

    // ── Relationships in status view ───────────────────────────────

    [Fact]
    public void FormatWorkItem_WithLinks_ShowsRelationshipsSection()
    {
        var item = CreateWorkItem(42, "Linked Item", "Active");
        var links = new List<WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 200, "Predecessor"),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null, links: links);

        result.ShouldContain("Relationships");
        result.ShouldContain("Related");
        result.ShouldContain("#100");
        result.ShouldContain("Predecessor");
        result.ShouldContain("#200");
    }

    [Fact]
    public void FormatWorkItem_WithNoRelationships_OmitsSection()
    {
        var item = CreateWorkItem(42, "Empty", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null);

        result.ShouldNotContain("Relationships");
    }

    [Fact]
    public void FormatWorkItem_WithParent_ShowsParentInRelationships()
    {
        var item = CreateWorkItem(42, "Child Task", "Active");
        var parentItem = new WorkItem
        {
            Id = 10, Title = "Parent Epic", State = "Doing",
            Type = WorkItemType.Epic,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null, parent: parentItem);

        result.ShouldContain("Relationships");
        result.ShouldContain("Parent");
        result.ShouldContain("#10");
        result.ShouldContain("Parent Epic");
    }

    [Fact]
    public void FormatWorkItem_WithChildren_ShowsChildrenInRelationships()
    {
        var item = CreateWorkItem(10, "Parent Issue", "Active");
        var children = new List<WorkItem>
        {
            CreateWorkItem(20, "Task A", "Done"),
            CreateWorkItem(21, "Task B", "To Do"),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null, children: children);

        result.ShouldContain("Relationships");
        result.ShouldContain("Child");
        result.ShouldContain("#20");
        result.ShouldContain("Task A");
        result.ShouldContain("#21");
        result.ShouldContain("Task B");
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

        result.ShouldContain("\uF091"); // icon_trophy → nf-fa-trophy in nerd mode (+ trailing space from NormalizeBadgeWidth)
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

    // ── EPIC-003: Unified state color resolution ────────────────────

    [Theory]
    [InlineData("Design", "InProgress", "\x1b[34m")]   // Blue — InProgress via entries
    [InlineData("Review", "Resolved", "\x1b[32m")]     // Green — Resolved via entries
    [InlineData("Ideation", "Proposed", "\x1b[2m")]    // Dim — Proposed via entries
    [InlineData("Discarded", "Removed", "\x1b[31m")]   // Red — Removed via entries
    public void FormatWorkItem_CustomState_WithStateEntries_UsesEntryCategory(
        string stateName, string adoCategory, string expectedAnsi)
    {
        var category = StateCategoryResolver.ParseCategory(adoCategory);
        var entries = new List<StateEntry>
        {
            new(stateName, category, null),
        };
        var formatter = new HumanOutputFormatter(new DisplayConfig(), typeAppearances: null, stateEntries: entries);
        var item = CreateWorkItem(300, "Custom State Item", stateName);

        var result = formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain(expectedAnsi);
        result.ShouldContain(stateName);
    }

    [Fact]
    public void FormatWorkItem_CustomState_WithoutEntries_FallsBackToUnknown()
    {
        // Without state entries, custom states fall back to Reset (Unknown)
        var formatter = new HumanOutputFormatter();
        var item = CreateWorkItem(301, "No Entries", "Design");

        var result = formatter.FormatWorkItem(item, showDirty: false);

        // "Design" is not a known state — falls back to Reset
        result.ShouldContain("\x1b[0m");
    }

    [Fact]
    public void HumanOutputFormatter_And_SpectreTheme_ResolvesSameCategoryForCustomState()
    {
        // Both renderers should resolve the same state category when given the same entries
        var entries = new List<StateEntry>
        {
            new("Design", Domain.Enums.StateCategory.InProgress, null),
            new("Review", Domain.Enums.StateCategory.Resolved, null),
        };
        var displayConfig = new DisplayConfig();

        var humanFormatter = new HumanOutputFormatter(displayConfig, typeAppearances: null, stateEntries: entries);
        var item = CreateWorkItem(302, "Design Item", "Design");
        var humanResult = humanFormatter.FormatWorkItem(item, showDirty: false);

        // HumanOutputFormatter uses Blue (\x1b[34m) for InProgress
        humanResult.ShouldContain("\x1b[34m");

        // SpectreTheme resolves the same category via StateCategoryResolver
        var resolvedCategory = StateCategoryResolver.Resolve("Design", entries);
        resolvedCategory.ShouldBe(Domain.Enums.StateCategory.InProgress);
    }

    // ── EPIC-002: Status summary header line ────────────────────────

    [Fact]
    public void FormatStatusSummary_ContainsIdTypeAndTitle()
    {
        var item = CreateWorkItem(12345, "Fix login timeout", "Active");

        var result = _formatter.FormatStatusSummary(item);

        result.ShouldContain("#12345");
        result.ShouldContain("●");
        result.ShouldContain("Task");
        result.ShouldContain("Fix login timeout");
        result.ShouldContain("Active");
    }

    [Fact]
    public void FormatStatusSummary_ContainsAnsiColors()
    {
        var item = CreateWorkItem(1, "Test", "Active");

        var result = _formatter.FormatStatusSummary(item);

        result.ShouldContain("\x1b["); // ANSI escapes present
        result.ShouldContain("\x1b[36m"); // Cyan for ● marker
    }

    [Fact]
    public void FormatStatusSummary_IncludesEmDashSeparator()
    {
        var item = CreateWorkItem(1, "My Task", "New");

        var result = _formatter.FormatStatusSummary(item);

        result.ShouldContain("—"); // em dash
    }

    [Fact]
    public void FormatStatusSummary_WrapsStateInBrackets()
    {
        var item = CreateWorkItem(1, "My Task", "Closed");

        var result = _formatter.FormatStatusSummary(item);

        // State should appear inside square brackets
        result.ShouldContain("[");
        result.ShouldContain("Closed");
        result.ShouldContain("]");
    }

    [Fact]
    public void FormatStatusSummary_JsonFormatter_ReturnsEmpty()
    {
        var jsonFormatter = new JsonOutputFormatter();
        var item = CreateWorkItem(1, "Test", "Active");

        var result = jsonFormatter.FormatStatusSummary(item);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void FormatStatusSummary_MinimalFormatter_ReturnsEmpty()
    {
        var minimalFormatter = new MinimalOutputFormatter();
        var item = CreateWorkItem(1, "Test", "Active");

        var result = minimalFormatter.FormatStatusSummary(item);

        result.ShouldBeEmpty();
    }

    // ── Effort display in tree (EPIC-007 E2-T10) ───────────────────

    [Fact]
    public void FormatTree_ChildWithStoryPoints_ShowsEffortInline()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(10, "Child Task", "New");
        child.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
        });

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });
        var result = _formatter.FormatTree(tree, 10, null);

        result.ShouldContain("(5 pts)");
    }

    [Fact]
    public void FormatTree_ChildWithEffortField_ShowsEffortInline()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(10, "Scrum Task", "New");
        child.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.Effort"] = "8",
        });

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });
        var result = _formatter.FormatTree(tree, 10, null);

        result.ShouldContain("(8 pts)");
    }

    [Fact]
    public void FormatTree_ChildWithSizeField_ShowsEffortInline()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(10, "CMMI Task", "New");
        child.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.Size"] = "3",
        });

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });
        var result = _formatter.FormatTree(tree, 10, null);

        result.ShouldContain("(3 pts)");
    }

    [Fact]
    public void FormatTree_ChildWithoutEffort_NoEffortShown()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(10, "Plain Task", "New");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });
        var result = _formatter.FormatTree(tree, 10, null);

        result.ShouldNotContain("pts");
    }

    [Fact]
    public void FormatTree_ChildWithEmptyEffort_NoEffortShown()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(10, "Empty Effort", "New");
        child.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "",
        });

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });
        var result = _formatter.FormatTree(tree, 10, null);

        result.ShouldNotContain("pts");
    }

    // ── Extended fields in FormatWorkItem (EPIC-007 E2-T10) ─────────

    [Fact]
    public void FormatWorkItem_WithExtendedFields_ShowsExtendedSection()
    {
        var item = CreateWorkItem(1, "Extended Item", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs);

        result.ShouldContain("Extended");
        result.ShouldContain("Priority");
        result.ShouldContain("Story Points");
    }

    [Fact]
    public void FormatWorkItem_NoFieldDefinitions_DeriveDisplayNames()
    {
        var item = CreateWorkItem(1, "Derive Names", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null);

        result.ShouldContain("Priority");
        result.ShouldContain("1");
    }

    // ── Status fields config rendering integration (EPIC-010 E3) ────

    [Fact]
    public void GetExtendedFields_WithStatusEntries_ShowsOnlyStarredInOrder()
    {
        var item = CreateWorkItem(1, "Config Item", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            ["System.Tags"] = "backend",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
            new("System.Tags", "Tags", "string", false),
        };

        // Only Story Points is starred
        var entries = new StatusFieldEntry[]
        {
            new("Microsoft.VSTS.Scheduling.StoryPoints", true),
            new("Microsoft.VSTS.Common.Priority", false),
            new("System.Tags", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs, entries);

        result.ShouldContain("Story Points");
        result.ShouldContain("5");
        result.ShouldNotContain("Priority:");
        result.ShouldNotContain("Tags:");
    }

    [Fact]
    public void GetExtendedFields_WithoutEntries_PreservesCurrentBehavior()
    {
        var item = CreateWorkItem(1, "Default Item", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "3",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries: null);

        result.ShouldContain("Priority");
        result.ShouldContain("Story Points");
    }

    [Fact]
    public void GetExtendedFields_UnknownRefName_SilentlySkipped()
    {
        var item = CreateWorkItem(1, "Unknown Ref", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
        });

        var entries = new StatusFieldEntry[]
        {
            new("Custom.NonExistent.Field", true),
            new("Microsoft.VSTS.Common.Priority", true),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null, statusFieldEntries: entries);

        result.ShouldContain("Priority");
        result.ShouldNotContain("NonExistent");
    }

    [Fact]
    public void GetExtendedFields_AllUnstarred_NoExtendedSection()
    {
        var item = CreateWorkItem(1, "AllUnstarred", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
        });

        var entries = new StatusFieldEntry[]
        {
            new("Microsoft.VSTS.Common.Priority", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefinitions: null, statusFieldEntries: entries);

        result.ShouldNotContain("Extended");
        result.ShouldNotContain("Priority:");
    }

    // ── Description section rendering (sync path) ────────────────────

    [Fact]
    public void FormatWorkItem_WithDescription_ShowsDescriptionSection()
    {
        var item = CreateWorkItem(1, "Described Item", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>This is a test description.</p>",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("── Description ──");
        result.ShouldContain("This is a test description.");
    }

    [Fact]
    public void FormatWorkItem_WithMultiLineDescription_RendersAllLines()
    {
        var item = CreateWorkItem(1, "Multi Line", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Line one</p><p>Line two</p><p>Line three</p>",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldContain("── Description ──");
        result.ShouldContain("Line one");
        result.ShouldContain("Line two");
        result.ShouldContain("Line three");
    }

    [Fact]
    public void FormatWorkItem_MultilineDescription_IndentedWith2Spaces()
    {
        var item = CreateWorkItem(1, "Indent Check", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>First</p><p>Second</p>",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false);
        var stripped = StripAnsi(result);

        stripped.ShouldContain("  First");
        stripped.ShouldContain("  Second");
    }

    [Fact]
    public void FormatWorkItem_WithNullDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(1, "No Desc", "Active");

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("── Description ──");
    }

    [Fact]
    public void FormatWorkItem_WithWhitespaceDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(1, "Whitespace Desc", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "   ",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("── Description ──");
    }

    [Fact]
    public void FormatWorkItem_WithHtmlOnlyDescription_NoDescriptionSection()
    {
        var item = CreateWorkItem(1, "Html Only", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>  </p>",
        });

        var result = _formatter.FormatWorkItem(item, showDirty: false);

        result.ShouldNotContain("── Description ──");
    }

    [Fact]
    public void FormatWorkItem_DescriptionExcludedFromExtendedFields_WhenStarred()
    {
        var item = CreateWorkItem(1, "Starred Desc", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Some description</p>",
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("System.Description", "Description", "html", false),
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        };

        var entries = new StatusFieldEntry[]
        {
            new("System.Description", true),
            new("Microsoft.VSTS.Common.Priority", true),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs, entries);

        // Description should appear in dedicated section, not in extended fields grid
        result.ShouldContain("── Description ──");
        result.ShouldContain("Some description");
        result.ShouldContain("Priority:");
        // Count occurrences of "Some description" — should appear only once (in dedicated section)
        var occurrences = result.Split("Some description").Length - 1;
        occurrences.ShouldBe(1);
    }

    [Fact]
    public void FormatWorkItem_DescriptionExcludedFromExtendedFields_AutoDetection()
    {
        var item = CreateWorkItem(1, "Auto Desc", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Auto detected desc</p>",
            ["Custom.Field"] = "custom value",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("System.Description", "Description", "html", false),
            new("Custom.Field", "Custom Field", "string", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs, statusFieldEntries: null);

        // Description should be in dedicated section
        result.ShouldContain("── Description ──");
        result.ShouldContain("Auto detected desc");
        // Custom field still shows in extended section
        result.ShouldContain("custom value");
    }

    [Fact]
    public void FormatWorkItem_DescriptionAppearsAfterExtendedBeforeProgress()
    {
        var item = CreateWorkItem(1, "Order Check", "Active");
        item.ImportFields(new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Desc content</p>",
            ["Microsoft.VSTS.Common.Priority"] = "1",
        });

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
        };

        var result = _formatter.FormatWorkItem(item, showDirty: false, fieldDefs,
            statusFieldEntries: null, childProgress: (2, 5));

        var extIdx = result.IndexOf("── Extended ──", StringComparison.Ordinal);
        var descIdx = result.IndexOf("── Description ──", StringComparison.Ordinal);
        var progIdx = result.IndexOf("Progress:", StringComparison.Ordinal);

        extIdx.ShouldBeGreaterThan(-1);
        descIdx.ShouldBeGreaterThan(-1);
        progIdx.ShouldBeGreaterThan(-1);

        // Extended < Description < Progress
        descIdx.ShouldBeGreaterThan(extIdx);
        progIdx.ShouldBeGreaterThan(descIdx);
    }

    // ── EPIC-001: Sprint progress footer & category separators ──────

    [Fact]
    public void FormatWorkspace_MixedStates_ShowsProgressFooter()
    {
        var items = new[]
        {
            CreateWorkItem(1, "New Item", "New"),
            CreateWorkItem(2, "Active Item", "Active"),
            CreateWorkItem(3, "Resolved Item", "Resolved"),
            CreateWorkItem(4, "Closed Item", "Closed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        plain.ShouldContain("Sprint:");
        plain.ShouldContain("2/4");
        plain.ShouldContain("done");
        plain.ShouldContain("1 in progress");
        plain.ShouldContain("1 proposed");
    }

    [Fact]
    public void FormatWorkspace_AllProposed_ShowsZeroDone()
    {
        var items = new[]
        {
            CreateWorkItem(1, "New 1", "New"),
            CreateWorkItem(2, "New 2", "New"),
            CreateWorkItem(3, "New 3", "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        plain.ShouldContain("0/3");
        plain.ShouldContain("done");
        plain.ShouldContain("3 proposed");
    }

    [Fact]
    public void FormatWorkspace_AllComplete_ShowsAllDone()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Done 1", "Closed"),
            CreateWorkItem(2, "Done 2", "Closed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        plain.ShouldContain("2/2");
        plain.ShouldContain("done");
        plain.ShouldNotContain("in progress");
        plain.ShouldNotContain("proposed");
    }

    [Fact]
    public void FormatWorkspace_EmptySprint_NoProgressFooter()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        // No "Sprint: " progress footer when there are zero items
        // The header "Sprint (0 items):" still exists but "Sprint: " (with colon-space) is footer-exclusive
        result.ShouldNotContain("Sprint: ");
    }

    [Fact]
    public void FormatWorkspace_MultipleCategoryGroups_HasSeparators()
    {
        var items = new[]
        {
            CreateWorkItem(1, "New Item", "New"),
            CreateWorkItem(2, "Active Item", "Active"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        // Separator should appear between Proposed and In Progress groups
        result.ShouldContain("────");
    }

    [Fact]
    public void FormatWorkspace_SingleCategoryGroup_NoSeparator()
    {
        var items = new[]
        {
            CreateWorkItem(1, "New 1", "New"),
            CreateWorkItem(2, "New 2", "New"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);

        // Only one category group, so no category separator (indented ────)
        // The top-level ──── header is a different line
        var lines = result.Split('\n');
        var separatorCount = lines.Count(l => StripAnsi(l).Trim() == "────");
        separatorCount.ShouldBe(0);
    }

    [Fact]
    public void FormatWorkspace_ResolvedAndCompleted_BothCountAsDone()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Resolved", "Resolved"),
            CreateWorkItem(2, "Closed", "Closed"),
            CreateWorkItem(3, "Active", "Active"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        // Resolved + Completed = 2 done out of 3 total
        plain.ShouldContain("2/3");
        plain.ShouldContain("done");
    }

    [Fact]
    public void FormatWorkspace_RemovedState_CountsAsProposed()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Active Item", "Active"),
            CreateWorkItem(2, "Removed Item", "Removed"),
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        // Removed items are bucketed with Proposed — total=2, done=0, in progress=1, proposed=1
        plain.ShouldContain("Sprint:");
        plain.ShouldContain("0/2");
        plain.ShouldContain("done");
        plain.ShouldContain("1 in progress");
        plain.ShouldContain("1 proposed");
    }

    [Fact]
    public void FormatWorkspace_UnknownState_CountsAsProposed()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Active Item", "Active"),
            CreateWorkItem(2, "Custom State", "SomeCustomState"), // → Unknown → Proposed bucket
        };
        var ws = Workspace.Build(null, items, Array.Empty<WorkItem>());

        var result = _formatter.FormatWorkspace(ws, staleDays: 14);
        var plain = StripAnsi(result);

        plain.ShouldContain("0/2");
        plain.ShouldContain("1 in progress");
        plain.ShouldContain("1 proposed");
    }

    // ── EPIC-002: State-Colored Tree Connectors & Link Differentiation ──

    [Fact]
    public void FormatTree_ChildConnectorsIncludeStateColor_Active()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "In Progress Child", "Active");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Active → InProgress → Blue (\x1b[34m)
        result.ShouldContain("\x1b[34m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_ChildConnectorsIncludeStateColor_Closed()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "Closed Child", "Closed");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Closed → Completed → Green (\x1b[32m)
        result.ShouldContain("\x1b[32m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_ChildConnectorsIncludeStateColor_New()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "New Child", "New");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // New → Proposed → Dim (\x1b[2m)
        result.ShouldContain("\x1b[2m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_ChildConnectorsIncludeStateColor_Removed()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "Removed Child", "Removed");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Removed → Red (\x1b[31m)
        result.ShouldContain("\x1b[31m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_MultipleChildren_ConnectorsColoredByState()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child1 = CreateWorkItem(2, "Active Child", "Active");
        var child2 = CreateWorkItem(3, "Closed Child", "Closed");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // First child: Active → Blue with ├──
        result.ShouldContain("\x1b[34m├── \x1b[0m");
        // Last child: Closed → Green with └──
        result.ShouldContain("\x1b[32m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_UnknownState_FallsBackToDefaultConnectorColor()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var child = CreateWorkItem(2, "Unknown Child", "SomeUnknownState");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Unknown state → StateCategoryResolver returns Unknown → GetStateColor returns Reset (\x1b[0m)
        // REQ-005: connector wrapped in Reset for unknown state categories
        result.ShouldContain("\x1b[0m└── \x1b[0m");
        var stripped = StripAnsi(result);
        stripped.ShouldContain("└── ");
    }

    [Fact]
    public void FormatTree_EmptyState_FallsBackToDimConnectorColor()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        // Empty state is a distinct code path from unknown — resolves to Dim (\x1b[2m)
        var child = CreateWorkItem(2, "Empty State Child", "");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Empty state → GetStateColor returns Dim (\x1b[2m)
        result.ShouldContain("\x1b[2m└── \x1b[0m");
    }

    [Fact]
    public void FormatTree_LinksSection_UsesBlueForLinkType()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var links = new[] { new WorkItemLink(1, 42, "Related") };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            focusedItemLinks: links);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Link type should be wrapped in Blue ANSI
        result.ShouldContain($"\x1b[34mRelated\x1b[0m: #42");
    }

    [Fact]
    public void FormatTree_LinksSection_ContainsSwapGlyph()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var links = new[] { new WorkItemLink(1, 42, "Related") };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            focusedItemLinks: links);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // Links header should contain ⇄ glyph
        result.ShouldContain("⇄ Links");
    }

    [Fact]
    public void FormatTree_LinksSection_MultipleLinks_AllBlue()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var links = new[]
        {
            new WorkItemLink(1, 42, "Related"),
            new WorkItemLink(1, 99, "Predecessor"),
        };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            focusedItemLinks: links);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        result.ShouldContain($"\x1b[34mRelated\x1b[0m: #42");
        result.ShouldContain($"\x1b[34mPredecessor\x1b[0m: #99");
    }

    [Fact]
    public void FormatTree_LinksHeader_NotFullyDim()
    {
        var focus = CreateWorkItem(1, "Focus", "Active");
        var links = new[] { new WorkItemLink(1, 10, "Successor") };
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            focusedItemLinks: links);

        var result = _formatter.FormatTree(tree, maxChildren: 10, activeId: null);

        // The links header should NOT be wrapped entirely in Dim
        result.ShouldNotContain($"{Dim}╰── Links{Reset}");
        // Should contain the ⇄ glyph prefix
        result.ShouldContain("╰── ⇄ Links");
    }

    // Expose ANSI constants for test assertions
    private const string Dim = "\x1b[2m";
    private const string Reset = "\x1b[0m";
    private const string Green = "\x1b[32m";
    private const string Bold = "\x1b[1m";

    // ── Flow-Start Panel (EPIC-003) ────────────────────────────────

    [Fact]
    public void FormatFieldChange_TransitionArrowIsGreenColored()
    {
        var change = new FieldChange("System.State", "New", "Active");

        var result = _formatter.FormatFieldChange(change);

        result.ShouldContain($"{Green}→{Reset}");
    }

    [Fact]
    public void FormatFlowSummary_ContainsBoxDrawingCharacters()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain("┌");
        result.ShouldContain("┐");
        result.ShouldContain("└");
        result.ShouldContain("┘");
        result.ShouldContain("│");
        result.ShouldContain("─");
    }

    [Fact]
    public void FormatFlowSummary_ContainsStateTransitionWithArrow()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain("State:");
        result.ShouldContain("New");
        result.ShouldContain($"{Green}→{Reset}");
        result.ShouldContain("Active");
    }

    [Fact]
    public void FormatFlowSummary_ContainsBranchName()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain("Branch:");
        result.ShouldContain("feature/123-add-login");
    }

    [Fact]
    public void FormatFlowSummary_ContainsContext()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain("Context:");
        result.ShouldContain("set to #123");
    }

    [Fact]
    public void FormatFlowSummary_ContainsSuccessHeader()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain($"{Green}✓{Reset}");
        result.ShouldContain("Flow started for #123");
        result.ShouldContain("Add login");
    }

    [Fact]
    public void FormatFlowSummary_ContainsSummaryHeader()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");

        result.ShouldContain($"{Bold} Summary {Reset}");
    }

    [Fact]
    public void FormatFlowSummary_NullBranch_OmitsBranchRow()
    {
        var result = _formatter.FormatFlowSummary(42, "Test item", "New", "Active", null);

        result.ShouldNotContain("Branch:");
        result.ShouldContain("State:");
        result.ShouldContain("Context:");
    }

    [Fact]
    public void FormatFlowSummary_NoStateTransition_ShowsCurrentState()
    {
        var result = _formatter.FormatFlowSummary(42, "Test item", "Active", null, null);

        result.ShouldContain("State:");
        result.ShouldContain("Active");
        // No arrow when no transition
        result.ShouldNotContain("→");
    }

    [Fact]
    public void FormatFlowSummary_BoxBordersAreAligned()
    {
        var result = _formatter.FormatFlowSummary(123, "Add login", "New", "Active", "feature/123-add-login");
        var lines = result.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Find the top border (starts with ┌) and bottom border (starts with └)
        var topLine = lines.First(l => l.StartsWith("┌"));
        var bottomLine = lines.First(l => l.StartsWith("└"));
        var contentLines = lines.Where(l => l.StartsWith("│")).ToArray();

        // Strip ANSI escape sequences for length comparison
        static string StripAnsi(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    i += 2;
                    while (i < s.Length && s[i] != 'm') i++;
                    if (i < s.Length) i++;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        var topVisibleLen = StripAnsi(topLine).Length;
        var bottomVisibleLen = StripAnsi(bottomLine).Length;
        topVisibleLen.ShouldBe(bottomVisibleLen, "Top and bottom borders should have same visible width");

        foreach (var line in contentLines)
        {
            var visibleLen = StripAnsi(line).Length;
            visibleLen.ShouldBe(topVisibleLen, $"Content line should match border width: {StripAnsi(line)}");
        }
    }

    // ── Status Panel Enhancements (EPIC-004 ITEM-020) ───────────────

    [Fact]
    public void FormatWorkItem_ParentWithChildren_ShowsProgressBar()
    {
        var item = CreateWorkItem(1, "Parent Story", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            childProgress: (4, 6));

        result.ShouldContain("Progress:");
        result.ShouldContain("████");
        result.ShouldContain("░░");
        result.ShouldContain("4/6");
    }

    [Fact]
    public void FormatWorkItem_LeafItem_NoProgressBar()
    {
        var item = CreateWorkItem(1, "Leaf Task", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            childProgress: null);

        result.ShouldNotContain("Progress:");
        result.ShouldNotContain("█");
    }

    [Fact]
    public void FormatWorkItem_ZeroChildren_NoProgressBar()
    {
        var item = CreateWorkItem(1, "No Children", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            childProgress: (0, 0));

        result.ShouldNotContain("Progress:");
    }

    [Fact]
    public void FormatWorkItem_AllChildrenDone_GreenProgressBar()
    {
        var item = CreateWorkItem(1, "Complete Parent", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            childProgress: (5, 5));

        result.ShouldContain("Progress:");
        result.ShouldContain($"{Green}"); // Green ANSI for complete
        result.ShouldContain("5/5");
    }

    [Fact]
    public void FormatWorkItem_PendingChanges_ShowsConsolidatedFooter()
    {
        var item = CreateWorkItem(1, "With Changes", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: (3, 2));

        result.ShouldContain($"{Dim}3 field changes, 2 notes staged{Reset}");
    }

    [Fact]
    public void FormatWorkItem_NoPendingChanges_NoFooter()
    {
        var item = CreateWorkItem(1, "No Changes", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: null);

        result.ShouldNotContain("field change");
        result.ShouldNotContain("staged");
    }

    [Fact]
    public void FormatWorkItem_BothProgressAndPendingChanges()
    {
        var item = CreateWorkItem(1, "Full Status", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            childProgress: (2, 4),
            pendingChanges: (1, 3));

        result.ShouldContain("Progress:");
        result.ShouldContain("2/4");
        result.ShouldContain("1 field change, 3 notes staged");
    }

    [Fact]
    public void FormatWorkItem_PendingChanges_OnlyFields_NoNoteSegment()
    {
        var item = CreateWorkItem(1, "Fields Only", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: (2, 0));

        result.ShouldContain($"{Dim}2 field changes{Reset}");
        result.ShouldNotContain("note");
        result.ShouldNotContain("staged");
    }

    [Fact]
    public void FormatWorkItem_PendingChanges_OnlyNotes_NoFieldSegment()
    {
        var item = CreateWorkItem(1, "Notes Only", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: (0, 3));

        result.ShouldContain($"{Dim}3 notes staged{Reset}");
        result.ShouldNotContain("field change");
    }

    [Fact]
    public void FormatWorkItem_PendingChanges_SingularNoteCount()
    {
        var item = CreateWorkItem(1, "Singular Note", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: (2, 1));

        result.ShouldContain("1 note staged");
        result.ShouldNotContain("notes staged");
    }

    [Fact]
    public void FormatWorkItem_PendingChanges_ZeroBoth_NoFooter()
    {
        var item = CreateWorkItem(1, "Zero Both", "Active");
        var result = _formatter.FormatWorkItem(item, showDirty: false,
            fieldDefinitions: null, statusFieldEntries: null,
            pendingChanges: (0, 0));

        result.ShouldNotContain("field change");
        result.ShouldNotContain("staged");
    }

    // ── EPIC-005: Glyph Consistency Tests ─────────────────────────────

    [Fact]
    public void FormatHint_StartsWithYellowArrowGlyph()
    {
        var result = _formatter.FormatHint("run twig refresh");

        // TEST-004: FormatHint output MUST start with new glyph prefix
        result.ShouldStartWith("\x1b[33m→\x1b[0m ");
        result.ShouldContain("hint: run twig refresh");
    }

    [Fact]
    public void FormatError_StartsWithCrossGlyph()
    {
        var result = _formatter.FormatError("not found");

        // TEST-005: FormatError output MUST start with ✗ error: prefix
        result.ShouldStartWith("\x1b[31m✗ error:\x1b[0m");
        result.ShouldContain("not found");
    }

    [Fact]
    public void FormatWorkItem_DirtyMarker_UsesPencilGlyph()
    {
        var item = CreateWorkItem(1, "Edited Item", "Active");
        item.UpdateField("System.Title", "New Title");
        item.ApplyCommands();

        var result = _formatter.FormatWorkItem(item, showDirty: true);

        // TEST-009: Dirty marker MUST render as ✎ (U+270E) instead of •
        result.ShouldContain("✎");
        result.ShouldNotContain("•");
    }

    [Fact]
    public void FormatDisambiguation_Enriched_ShowsTypeBadgeAndState()
    {
        var matches = new List<(int Id, string Title, string? TypeName, string? State)>
        {
            (100, "Login Bug", "Bug", "Active"),
            (101, "Feature Request", "Feature", "New"),
            (102, "Simple Task", null, null),
        };

        var result = _formatter.FormatDisambiguation(matches);

        result.ShouldContain("Multiple matches:");
        result.ShouldContain("#100");
        result.ShouldContain("#101");
        result.ShouldContain("#102");
        result.ShouldContain("Login Bug");
        // State should appear for items with state
        result.ShouldContain("[");
        result.ShouldContain("Active");
        result.ShouldContain("New");
        // Item without type/state should still render
        result.ShouldContain("Simple Task");
    }

    [Fact]
    public void FormatDisambiguation_Enriched_NullTypeAndState_FallsBackGracefully()
    {
        var matches = new List<(int Id, string Title, string? TypeName, string? State)>
        {
            (200, "Plain Item", null, null),
        };

        var result = _formatter.FormatDisambiguation(matches);

        result.ShouldContain("[1]");
        result.ShouldContain("#200");
        result.ShouldContain("Plain Item");
    }

    // ── FormatQueryResults (Task 3.2) ───────────────────────────────

    [Fact]
    public void FormatQueryResults_ZeroItems_ReturnsNoItemsFound()
    {
        var result = new QueryResult(Array.Empty<WorkItem>(), IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("No items found");
    }

    [Fact]
    public void FormatQueryResults_SingleItem_ContainsIdAndTitle()
    {
        var items = new[] { CreateWorkItem(42, "Fix login bug", "Active", "Alice") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("42");
        output.ShouldContain("Fix login bug");
        output.ShouldContain("Alice");
    }

    [Fact]
    public void FormatQueryResults_MultipleItems_ContainsSummaryLine()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Item A", "Active", "Alice"),
            CreateWorkItem(2, "Item B", "Closed", "Bob"),
            CreateWorkItem(3, "Item C", "New"),
        };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("Found 3 item(s)");
    }

    [Fact]
    public void FormatQueryResults_Truncated_ShowsTruncationWarning()
    {
        var items = new[]
        {
            CreateWorkItem(1, "Item A", "Active"),
            CreateWorkItem(2, "Item B", "Active"),
        };
        var result = new QueryResult(items, IsTruncated: true);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("Showing top 2 results");
        output.ShouldContain("--top");
    }

    [Fact]
    public void FormatQueryResults_Truncated_SummaryShowsPlusIndicator()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: true);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("Found 1+ items");
    }

    [Fact]
    public void FormatQueryResults_NotTruncated_NoTruncationWarning()
    {
        var items = new[] { CreateWorkItem(1, "Item", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldNotContain("--top");
        output.ShouldNotContain("Showing top");
    }

    [Fact]
    public void FormatQueryResults_UnassignedItem_ShowsUnassignedLabel()
    {
        var items = new[] { CreateWorkItem(1, "Orphan task", "New") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("(unassigned)");
    }

    [Fact]
    public void FormatQueryResults_ContainsAnsiEscapes()
    {
        var items = new[] { CreateWorkItem(1, "Styled item", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("\x1b["); // ANSI escape present
    }

    [Fact]
    public void FormatQueryResults_ContainsTableBorders()
    {
        var items = new[] { CreateWorkItem(1, "Table item", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        // Spectre.Console Rounded border uses box-drawing characters
        output.ShouldContain("─");
    }

    [Fact]
    public void FormatQueryResults_ContainsColumnHeaders()
    {
        var items = new[] { CreateWorkItem(1, "Header check", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        var output = _formatter.FormatQueryResults(result);

        output.ShouldContain("ID");
        output.ShouldContain("Type");
        output.ShouldContain("Title");
        output.ShouldContain("State");
        output.ShouldContain("Assigned To");
    }

    [Fact]
    public void FormatQueryResults_SpecialCharactersInTitle_AreEscaped()
    {
        var items = new[] { CreateWorkItem(1, "Fix [urgent] bug & deploy", "Active") };
        var result = new QueryResult(items, IsTruncated: false);

        // Should not throw — Spectre markup special chars are escaped
        var output = _formatter.FormatQueryResults(result);

        output.ShouldNotBeNullOrEmpty();
    }

}
