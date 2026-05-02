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

    // ── BuildInteractiveTreeRenderable — 60-char edge cases ───────

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_MultipleSiblings_AllIdsVisible()
    {
        var budget = new WidthBudget(60);
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(10, LongTitle),
            CreateWorkItem(11, "Another very long title that should be truncated at narrow width"),
            CreateWorkItem(12, ShortTitle),
        };
        var state = CreateStateWithSiblings(siblings);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("#10");
        output.ShouldContain("#11");
        output.ShouldContain("#12");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_FilterMode_ShowsFilterBar()
    {
        var budget = new WidthBudget(60);
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha Task"),
            CreateWorkItem(2, "Beta Task"),
            CreateWorkItem(3, "Gamma Task"),
        };
        var state = CreateStateWithSiblings(siblings);
        state.ApplyFilter("Beta");

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("Filter:");
        output.ShouldContain("Beta");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_DeepParentChain_AllAncestorsTruncated()
    {
        var budget = new WidthBudget(60);
        var grandparent = CreateWorkItem(100, LongTitle, WorkItemType.Epic);
        var parent = CreateWorkItem(101, "Another extremely long parent title that needs truncation at sixty chars", WorkItemType.Issue);
        var child = CreateWorkItem(1, ShortTitle);
        var state = new TreeNavigatorState(
            child,
            new List<WorkItem> { grandparent, parent },
            new List<WorkItem> { child },
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldNotContain("exponential backoff");
        output.ShouldNotContain("needs truncation at sixty chars");
        output.ShouldContain("…");
        output.ShouldContain(ShortTitle);
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_ChildrenUnderParentChain_ChildTruncated()
    {
        var budget = new WidthBudget(60);
        var parent = CreateWorkItem(100, "Parent Epic", WorkItemType.Epic);
        var sibling = CreateWorkItem(1, ShortTitle);
        var child = CreateWorkItem(10, LongTitle);
        var state = new TreeNavigatorState(
            sibling,
            new List<WorkItem> { parent },
            new List<WorkItem> { sibling },
            new List<WorkItem> { child },
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("#10");
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_EmptySiblings_ShowsNoItems()
    {
        var budget = new WidthBudget(60);
        var state = new TreeNavigatorState(
            null,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("No items to display");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowBudget_CursorMarkerPresent()
    {
        var budget = new WidthBudget(60);
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, ShortTitle),
            CreateWorkItem(2, "Second item"),
        };
        var state = CreateStateWithSiblings(siblings);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 60);
        output.ShouldContain("❯");
    }

    // ── BuildInteractiveTreeRenderable — standard width (80) ────────

    [Fact]
    public void BuildInteractiveTreeRenderable_StandardBudget_ShortTitlePreserved()
    {
        var budget = new WidthBudget(80);
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, ShortTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 80);
        output.ShouldContain(ShortTitle);
        output.ShouldContain("#1");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_StandardBudget_LongTitleTruncated()
    {
        var budget = new WidthBudget(80);
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, LongTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 80);
        output.ShouldNotContain("exponential backoff");
        output.ShouldContain("…");
        output.ShouldContain("#1");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_StandardBudget_MediumTitleFits()
    {
        var budget = new WidthBudget(80);
        var mediumTitle = "Update user authentication flow for SSO";
        var state = CreateStateWithSiblings(
            new List<WorkItem> { CreateWorkItem(1, mediumTitle) });

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme, budget);

        var output = RenderToString(renderable, 80);
        output.ShouldContain(mediumTitle);
    }

    // ── Width comparison — narrow vs standard vs wide ────────────────

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowTruncatesMore_ThanStandard()
    {
        var title = new string('X', 60);
        var sibling = CreateWorkItem(1, title);
        var state60 = CreateStateWithSiblings(new List<WorkItem> { sibling });
        var state80 = CreateStateWithSiblings(new List<WorkItem> { sibling });

        var narrow = RenderToString(
            SpectreRenderer.BuildInteractiveTreeRenderable(state60, _theme, new WidthBudget(60)), 60);
        var standard = RenderToString(
            SpectreRenderer.BuildInteractiveTreeRenderable(state80, _theme, new WidthBudget(80)), 80);

        narrow.ShouldContain("…");
        standard.ShouldContain("…");
        narrow.Length.ShouldBeLessThan(standard.Length);
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NarrowVsWide_BothContainId()
    {
        var sibling = CreateWorkItem(42, LongTitle);
        var stateNarrow = CreateStateWithSiblings(new List<WorkItem> { sibling });
        var stateWide = CreateStateWithSiblings(new List<WorkItem> { sibling });

        var narrow = RenderToString(
            SpectreRenderer.BuildInteractiveTreeRenderable(stateNarrow, _theme, new WidthBudget(60)), 60);
        var wide = RenderToString(
            SpectreRenderer.BuildInteractiveTreeRenderable(stateWide, _theme, new WidthBudget(200)), 200);

        narrow.ShouldContain("#42");
        wide.ShouldContain("#42");
        wide.ShouldContain(LongTitle);
        narrow.ShouldNotContain(LongTitle);
    }

    // ── BuildPreviewPanel — 60-char edge cases ──────────────────────

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_WithLinks_LinksVisible()
    {
        var budget = new WidthBudget(60);
        var item = CreateWorkItem(1, ShortTitle);
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
            new(SourceId: 1, TargetId: 99, LinkType: "Predecessor"),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, links, Array.Empty<SeedLink>(), _theme, budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("Links:");
        output.ShouldContain("#42");
        output.ShouldContain("#99");
        output.ShouldContain("Related");
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_WithEffort_EffortVisible()
    {
        var budget = new WidthBudget(60);
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = ShortTitle,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.Effort"] = "5",
        });

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("Effort");
        output.ShouldContain("5");
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_IterationLastSegment_Visible()
    {
        var budget = new WidthBudget(60);
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = ShortTitle,
            State = "Active",
            IterationPath = IterationPath.Parse("MyProject\\Release 1\\Sprint 3").Value,
            AreaPath = AreaPath.Parse("MyProject").Value,
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("Sprint 3");
        output.ShouldContain("Iteration");
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_LinkJumpHighlight_Visible()
    {
        var budget = new WidthBudget(60);
        var item = CreateWorkItem(1, ShortTitle);
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, links, Array.Empty<SeedLink>(), _theme,
            linkJumpIndex: 0, budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("#42");
        output.ShouldContain("Related");
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_SeedLinks_Visible()
    {
        var budget = new WidthBudget(60);
        var item = CreateWorkItem(1, ShortTitle);
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -5, LinkType: "blocks", CreatedAt: DateTimeOffset.UtcNow),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), seedLinks, _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("#-5");
        output.ShouldContain("seed");
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_UnassignedItem_ShowsUnassigned()
    {
        var budget = new WidthBudget(60);
        var item = CreateWorkItem(1, ShortTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 60);
        output.ShouldContain("unassigned");
    }

    // ── BuildPreviewPanel — standard width (80) ─────────────────────

    [Fact]
    public void BuildPreviewPanel_StandardBudget_ShortTitlePreserved()
    {
        var budget = new WidthBudget(80);
        var item = CreateWorkItem(42, ShortTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 80);
        output.ShouldContain(ShortTitle);
        output.ShouldContain("#42");
    }

    [Fact]
    public void BuildPreviewPanel_StandardBudget_LongTitleTruncated()
    {
        var budget = new WidthBudget(80);
        var item = CreateWorkItem(42, LongTitle);

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
            budget: budget);

        var output = RenderToString(panel, 80);
        output.ShouldContain("…");
        output.ShouldNotContain("exponential backoff");
    }

    // ── BuildPreviewPanel — width comparison ────────────────────────

    [Fact]
    public void BuildPreviewPanel_NarrowVsWide_BothContainId()
    {
        var item = CreateWorkItem(42, LongTitle);

        var narrow = RenderToString(
            SpectreRenderer.BuildPreviewPanel(
                item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
                budget: new WidthBudget(60)), 60);
        var wide = RenderToString(
            SpectreRenderer.BuildPreviewPanel(
                item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
                budget: new WidthBudget(200)), 200);

        narrow.ShouldContain("#42");
        wide.ShouldContain("#42");
        wide.ShouldContain(LongTitle);
        narrow.ShouldNotContain(LongTitle);
    }

    [Fact]
    public void BuildPreviewPanel_NarrowBudget_TruncatesAssignee_NoBudgetPreserves()
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

        var narrow = RenderToString(
            SpectreRenderer.BuildPreviewPanel(
                item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme,
                budget: new WidthBudget(60)), 60);
        var noBudget = RenderToString(
            SpectreRenderer.BuildPreviewPanel(
                item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme), 200);

        narrow.ShouldNotContain(LongAssignee);
        noBudget.ShouldContain(LongAssignee);
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
