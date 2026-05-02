using Shouldly;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

/// <summary>
/// Tests that BuildInteractiveTreeRenderable, FormatSiblingLabel, and BuildPreviewPanel
/// correctly truncate titles and assigned-to fields when a WidthBudget is provided,
/// and preserve them at wide widths or when no budget is given.
/// </summary>
public sealed class InteractiveTreeWidthTests
{
    private const string ShortTitle = "Fix bug";
    private const string LongTitle = "Implement the advanced cross-service authentication middleware with retry logic and exponential backoff";
    private const string LongAssignee = "Bartholomew Jingleheimer Schmidt III";

    private readonly SpectreTheme _theme = new(new DisplayConfig());

    // ── BuildInteractiveTreeRenderable — narrow width ───────────────

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_TruncatesSiblingTitle()
    {
        var budget = new WidthBudget(60);
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, LongTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
        output.ShouldContain("#1");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_PreservesShortSiblingTitle()
    {
        var budget = new WidthBudget(60);
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, ShortTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain(ShortTitle);
        output.ShouldContain("#1");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_WideBudget_PreservesLongSiblingTitle()
    {
        var budget = new WidthBudget(200);
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, LongTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 200);
        output.ShouldContain(LongTitle);
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_TruncatesParentChainTitle()
    {
        var budget = new WidthBudget(60);
        var parent = CreateWorkItem(100, LongTitle, WorkItemType.Epic);
        var child = CreateWorkItem(1, ShortTitle);
        var state = new TreeNavigatorState(
            child,
            new List<WorkItem> { parent },
            new List<WorkItem> { child },
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_TruncatesChildTitle()
    {
        var budget = new WidthBudget(60);
        var siblings = new List<WorkItem> { CreateWorkItem(1, ShortTitle) };
        var children = new List<WorkItem> { CreateWorkItem(10, LongTitle) };
        var state = new TreeNavigatorState(
            siblings[0],
            Array.Empty<WorkItem>(),
            siblings,
            children,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
        output.ShouldContain("#10");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NoBudget_PreservesAllTitles()
    {
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, LongTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable, 200);
        output.ShouldContain(LongTitle);
    }

    // ── BuildPreviewPanel — title truncation with budget ─────────────

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_TruncatesTitle()
    {
        var budget = new WidthBudget(60);
        var item = CreateWorkItem(42, LongTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
        output.ShouldContain("#42");
    }

    [Fact]
    public void BuildPreviewPanel_WideBudget_PreservesLongTitle()
    {
        var budget = new WidthBudget(200);
        var item = CreateWorkItem(42, LongTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 200);
        output.ShouldContain(LongTitle);
    }

    [Fact]
    public void BuildPreviewPanel_NoBudget_FallsBackToDefault56()
    {
        // Without budget, the method falls back to titleBudget of 56
        var item = CreateWorkItem(42, LongTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme);

        var output = RenderToString(panel, 120);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    // ── BuildPreviewPanel — assigned-to truncation ───────────────────

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_TruncatesLongAssignee()
    {
        var budget = new WidthBudget(60);
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = ShortTitle,
            State = "Active",
            AssignedTo = LongAssignee,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("…");
        output.ShouldNotContain(LongAssignee);
    }

    [Fact]
    public void BuildPreviewPanel_NoBudget_PreservesFullAssignee()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = ShortTitle,
            State = "Active",
            AssignedTo = LongAssignee,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme);

        var output = RenderToString(panel, 120);
        output.ShouldContain(LongAssignee);
    }

    // ── BuildPreviewPanel — null item still works ────────────────────

    [Fact]
    public void BuildPreviewPanel_NullItem_WithBudget_ShowsNoItemSelected()
    {
        var budget = new WidthBudget(60);

        var panel = SpectreRenderer.BuildPreviewPanel(
            null, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("No item selected");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string RenderToString(IRenderable renderable, int width = 120)
    {
        var console = new TestConsole { Profile = { Width = width } };
        console.Write(renderable);
        return console.Output;
    }

    private static TreeNavigatorState CreateStateWithSiblings(
        List<WorkItem> siblings, int cursorIndex = 0)
    {
        var cursorItem = siblings.Count > 0 && cursorIndex < siblings.Count
            ? siblings[cursorIndex]
            : null;
        return new TreeNavigatorState(
            cursorItem,
            Array.Empty<WorkItem>(),
            siblings,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());
    }

    private static WorkItem CreateWorkItem(int id, string title, WorkItemType? type = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = type ?? WorkItemType.Task,
            Title = title,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
