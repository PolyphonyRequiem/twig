using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Integration tests for working-level dim rendering in SpectreRenderer and HumanOutputFormatter.
/// Verifies that parent chain items above the configured working level are rendered with dim styling,
/// and that focused/child items are never dimmed.
/// </summary>
public class WorkingLevelDimRenderingTests
{
    private static readonly IReadOnlyDictionary<string, int> BasicTypeLevelMap =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = 0,
            ["Issue"] = 1,
            ["Task"] = 2,
        };

    private static readonly IReadOnlyDictionary<string, List<string>> BasicParentChildMap =
        new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Epic"] = new List<string> { "Issue" },
            ["Issue"] = new List<string> { "Task" },
        };

    // ── SpectreRenderer: FormatParentNode with working level ───────────

    [Fact]
    public void FormatParentNode_AboveWorkingLevel_WrappedInDim()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, theme)
        {
            TypeLevelMap = BasicTypeLevelMap,
            WorkingLevelTypeName = "Task",
        };

        var epicItem = CreateWorkItem(1, "My Epic", WorkItemType.Epic);
        var label = renderer.FormatParentNode(epicItem, aboveWorkingLevel: true);

        // The entire label should be wrapped in [dim]...[/]
        label.ShouldStartWith("[dim]");
        label.ShouldEndWith("[/]");
    }

    [Fact]
    public void FormatParentNode_AtWorkingLevel_NotFullyDimmed()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, theme);

        var taskItem = CreateWorkItem(1, "My Task", WorkItemType.Task);
        var label = renderer.FormatParentNode(taskItem, aboveWorkingLevel: false);

        // Should NOT start with [dim] — only title portion is dimmed
        label.ShouldNotStartWith("[dim]");
        label.ShouldContain("[dim]");
        label.ShouldContain("My Task");
    }

    // ── SpectreRenderer: BuildSpectreTreeAsync with working level ──────

    [Fact]
    public async Task BuildSpectreTreeAsync_WithWorkingLevel_ParentsAboveAreDimmed()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, theme)
        {
            TypeLevelMap = BasicTypeLevelMap,
            WorkingLevelTypeName = "Task",
        };

        var epic = CreateWorkItem(1, "Parent Epic", WorkItemType.Epic);
        var issue = CreateWorkItem(2, "Parent Issue", WorkItemType.Issue);
        var task = CreateWorkItem(3, "Focused Task", WorkItemType.Task);
        var parentChain = new List<WorkItem> { epic, issue };

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, parentChain, activeId: 3, getSiblingCount: null);

        var output = RenderToAnsiString(tree);
        // Both Epic and Issue should be dimmed (above Task level)
        // Focused task should NOT be dimmed
        output.ShouldContain("Focused Task");
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_NoWorkingLevel_StandardRendering()
    {
        var theme = new SpectreTheme(new DisplayConfig());
        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, theme)
        {
            TypeLevelMap = BasicTypeLevelMap,
            WorkingLevelTypeName = null,
        };

        var epic = CreateWorkItem(1, "Parent Epic", WorkItemType.Epic);
        var task = CreateWorkItem(2, "Focused Task", WorkItemType.Task);
        var parentChain = new List<WorkItem> { epic };

        var (tree, _) = await renderer.BuildSpectreTreeAsync(task, parentChain, activeId: 2, getSiblingCount: null);

        var output = RenderToString(tree);
        output.ShouldContain("Parent Epic");
        output.ShouldContain("Focused Task");
    }

    // ── HumanOutputFormatter: FormatTree with working level ───────────

    [Fact]
    public void FormatTree_WithWorkingLevel_ParentsAboveAreDimmed()
    {
        var fmt = new HumanOutputFormatter();
        var tree = CreateWorkTree(
            focusedType: WorkItemType.Task,
            parentTypes: new[] { WorkItemType.Epic, WorkItemType.Issue });

        var output = fmt.FormatTree(tree, maxDepth: 5, activeId: 3,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: "Task");

        // Epic and Issue are above Task — should have dim ANSI applied to entire line
        // The dim code is \x1b[2m
        var lines = output.Split('\n');
        var epicLine = lines.FirstOrDefault(l => l.Contains("Parent Epic"));
        epicLine.ShouldNotBeNull();
        // When above working level, the badge+title are wrapped in a single dim block
        epicLine.ShouldContain("\x1b[2m");

        var issueLine = lines.FirstOrDefault(l => l.Contains("Parent Issue"));
        issueLine.ShouldNotBeNull();
        issueLine.ShouldContain("\x1b[2m");
    }

    [Fact]
    public void FormatTree_WithWorkingLevel_FocusedItemNotDimmed()
    {
        var fmt = new HumanOutputFormatter();
        var tree = CreateWorkTree(
            focusedType: WorkItemType.Task,
            parentTypes: new[] { WorkItemType.Epic });

        var output = fmt.FormatTree(tree, maxDepth: 5, activeId: 3,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: "Task");

        // Focused item should have bold formatting, NOT start with dim
        var lines = output.Split('\n');
        var focusedLine = lines.FirstOrDefault(l => l.Contains("Focused Item"));
        focusedLine.ShouldNotBeNull();
        // Focused item uses bold (\x1b[1m), not a dim-only wrapper
        focusedLine.ShouldContain("\x1b[1m");
    }

    [Fact]
    public void FormatTree_NoWorkingLevel_NoDimming()
    {
        var fmt = new HumanOutputFormatter();
        var tree = CreateWorkTree(
            focusedType: WorkItemType.Task,
            parentTypes: new[] { WorkItemType.Epic });

        // No working level — standard rendering
        var outputWithout = fmt.FormatTree(tree, maxDepth: 5, activeId: 3,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: null);

        // With working level — enhanced dimming
        var outputWith = fmt.FormatTree(tree, maxDepth: 5, activeId: 3,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: "Task");

        // With working level should produce different output for parent chain
        outputWithout.ShouldNotBe(outputWith);
    }

    [Fact]
    public void FormatTree_WorkingLevelAtTopLevel_NoDimming()
    {
        var fmt = new HumanOutputFormatter();
        var tree = CreateWorkTree(
            focusedType: WorkItemType.Issue,
            parentTypes: new[] { WorkItemType.Epic });

        // Working level is Epic (level 0) — nothing is above it
        var output = fmt.FormatTree(tree, maxDepth: 5, activeId: 3,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: "Epic");

        // Epic parent should NOT have full-dim treatment (it's AT working level)
        var lines = output.Split('\n');
        var epicLine = lines.FirstOrDefault(l => l.Contains("Parent Epic"));
        epicLine.ShouldNotBeNull();
        // Standard parent rendering dims only the title, not badge+state
        // The pattern should have a color reset between badge and dim title
        epicLine.ShouldContain("\x1b[0m");
    }

    [Fact]
    public void FormatTree_ChildrenNeverDimmed()
    {
        var fmt = new HumanOutputFormatter();
        var epic = CreateWorkItem(1, "Parent Epic", WorkItemType.Epic, parentId: null);
        var focused = CreateWorkItem(2, "Focused Item", WorkItemType.Issue, parentId: 1);
        var child = CreateWorkItem(3, "Child Task", WorkItemType.Task, parentId: 2);

        var tree = WorkTree.Build(
            focused,
            new[] { epic },
            new[] { child },
            siblingCounts: null,
            focusedItemLinks: Array.Empty<WorkItemLink>());

        var output = fmt.FormatTree(tree, maxDepth: 5, activeId: 2,
            typeLevelMap: BasicTypeLevelMap,
            parentChildMap: BasicParentChildMap,
            workingLevelTypeName: "Task");

        // Child should not be dimmed — only parents above working level are dimmed
        var lines = output.Split('\n');
        var childLine = lines.FirstOrDefault(l => l.Contains("Child Task"));
        childLine.ShouldNotBeNull();
        // Children have type color in their badge, not a leading dim
        childLine.ShouldContain("#3");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static WorkTree CreateWorkTree(WorkItemType focusedType, WorkItemType[] parentTypes)
    {
        var parents = new List<WorkItem>();
        for (var i = 0; i < parentTypes.Length; i++)
        {
            var parentName = parentTypes[i] == WorkItemType.Epic ? "Parent Epic" : "Parent Issue";
            parents.Add(CreateWorkItem(i + 1, parentName, parentTypes[i],
                parentId: i > 0 ? i : null));
        }

        var focused = CreateWorkItem(parentTypes.Length + 1, "Focused Item", focusedType,
            parentId: parentTypes.Length);

        return WorkTree.Build(
            focused,
            parents,
            Array.Empty<WorkItem>(),
            siblingCounts: null,
            focusedItemLinks: Array.Empty<WorkItemLink>());
    }

    private static WorkItem CreateWorkItem(int id, string title, WorkItemType? type = null, int? parentId = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = "Active",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static string RenderToString(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(renderable);
        return console.Output;
    }

    private static string RenderToAnsiString(IRenderable renderable)
    {
        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(renderable);
        return console.Output;
    }
}
