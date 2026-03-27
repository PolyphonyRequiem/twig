using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class InteractiveTreeRenderTests
{
    private readonly SpectreTheme _theme = new(new DisplayConfig());

    // ── BuildInteractiveTreeRenderable ──────────────────────────────

    [Fact]
    public void BuildInteractiveTreeRenderable_CursorMarker_Present()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 1);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("❯");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_CursorItem_ContainsTypeBadge()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "My Task", WorkItemType.Task),
        };
        var state = CreateStateWithSiblings(siblings);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        // Badge for Task type should be present
        output.ShouldContain("Task");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_NavigateMode_ShowsKeybindingHelp()
    {
        var state = CreateStateWithSiblings(2);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("navigate");
        output.ShouldContain("Enter select");
        output.ShouldContain("Esc exit");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_FilterMode_ShowsFilterBar()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("test");

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("Filter:");
        output.ShouldContain("test");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_WithParentChain_ShowsDimmedAncestors()
    {
        var parent = CreateWorkItem(100, "Parent Epic", WorkItemType.Epic);
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Child 1"),
            CreateWorkItem(2, "Child 2"),
        };
        var state = new TreeNavigatorState(
            siblings[0],
            new List<WorkItem> { parent },
            siblings,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("Parent Epic");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_WithChildren_ShowsChildItems()
    {
        var siblings = new List<WorkItem> { CreateWorkItem(1, "Parent Task") };
        var children = new List<WorkItem>
        {
            CreateWorkItem(10, "Child A"),
            CreateWorkItem(11, "Child B"),
        };
        var state = new TreeNavigatorState(
            siblings[0],
            Array.Empty<WorkItem>(),
            siblings,
            children,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("Child A");
        output.ShouldContain("Child B");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_EmptySiblings_ShowsNoItemsMessage()
    {
        var state = new TreeNavigatorState(
            null,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("No items to display");
    }

    [Fact]
    public void BuildInteractiveTreeRenderable_TreePanelHeader_ContainsTree()
    {
        var state = CreateStateWithSiblings(1);

        var renderable = SpectreRenderer.BuildInteractiveTreeRenderable(state, _theme);

        var output = RenderToString(renderable);
        output.ShouldContain("Tree");
    }

    // ── BuildPreviewPanel ───────────────────────────────────────────

    [Fact]
    public void BuildPreviewPanel_NullItem_ShowsNoItemSelected()
    {
        var panel = SpectreRenderer.BuildPreviewPanel(
            null,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("No item selected");
    }

    [Fact]
    public void BuildPreviewPanel_WithItem_ShowsIdAndTitle()
    {
        var item = CreateWorkItem(42, "My Important Task");

        var panel = SpectreRenderer.BuildPreviewPanel(
            item,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("#42");
        output.ShouldContain("My Important Task");
    }

    [Fact]
    public void BuildPreviewPanel_WithItem_ShowsTypeAndState()
    {
        var item = CreateWorkItem(1, "Test Item");

        var panel = SpectreRenderer.BuildPreviewPanel(
            item,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("Type");
        output.ShouldContain("State");
        output.ShouldContain("Active");
    }

    [Fact]
    public void BuildPreviewPanel_UnassignedItem_ShowsUnassigned()
    {
        var item = CreateWorkItem(1, "Unassigned");

        var panel = SpectreRenderer.BuildPreviewPanel(
            item,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("unassigned");
    }

    [Fact]
    public void BuildPreviewPanel_WithAssignee_ShowsAssignee()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Assigned",
            State = "Active",
            AssignedTo = "Jane Doe",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("Jane Doe");
    }

    [Fact]
    public void BuildPreviewPanel_ShowsIterationLastSegment()
    {
        var item = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Task,
            Title = "Test",
            State = "Active",
            IterationPath = IterationPath.Parse("MyProject\\Release 1\\Sprint 3").Value,
            AreaPath = AreaPath.Parse("MyProject").Value,
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item,
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>(),
            _theme);

        var output = RenderToString(panel);
        output.ShouldContain("Sprint 3");
    }

    [Fact]
    public void BuildPreviewPanel_WithLinks_ShowsLinkTargetIds()
    {
        var item = CreateWorkItem(1, "Linked Item");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
            new(SourceId: 1, TargetId: 99, LinkType: "Predecessor"),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(item, links, Array.Empty<SeedLink>(), _theme);

        var output = RenderToString(panel);
        output.ShouldContain("Links:");
        output.ShouldContain("#42");
        output.ShouldContain("#99");
        output.ShouldContain("Related");
        output.ShouldContain("Predecessor");
    }

    [Fact]
    public void BuildPreviewPanel_WithSeedLinks_ShowsSeedAnnotation()
    {
        var item = CreateWorkItem(1, "Seed-Linked");
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -5, LinkType: "blocks", CreatedAt: DateTimeOffset.UtcNow),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), seedLinks, _theme);

        var output = RenderToString(panel);
        output.ShouldContain("Links:");
        output.ShouldContain("#-5");
        output.ShouldContain("seed");
    }

    [Fact]
    public void BuildPreviewPanel_NoLinks_DoesNotShowLinksSection()
    {
        var item = CreateWorkItem(1, "No Links");

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme);

        var output = RenderToString(panel);
        output.ShouldNotContain("Links:");
    }

    // ── BuildPreviewPanel — title truncation (I-003) ────────────────

    [Fact]
    public void BuildPreviewPanel_LongTitleWithBrackets_DoesNotProduceMalformedMarkup()
    {
        // Title with brackets that would expand under Markup.Escape — verifies I-003 fix
        var item = CreateWorkItem(42, "Fix [regression] in parser when handling [edge case] for long input strings that exceed");

        var panel = SpectreRenderer.BuildPreviewPanel(
            item, Array.Empty<WorkItemLink>(), Array.Empty<SeedLink>(), _theme);

        // Should not throw and should contain truncation indicator
        var output = RenderToString(panel);
        output.ShouldContain("#42");
        output.ShouldContain("...");
    }

    // ── ProcessKey (I-004) ──────────────────────────────────────────

    [Fact]
    public void ProcessKey_Enter_ReturnsCommitted()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 1);
        var key = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.Committed);
    }

    [Fact]
    public void ProcessKey_Escape_OutsideFilterMode_ReturnsCancelled()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.Cancelled);
    }

    [Fact]
    public void ProcessKey_Escape_InFilterMode_ClearsFilterAndReturnsFilterCleared()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("Item");
        state.IsFilterMode.ShouldBeTrue();
        var key = new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterCleared);
        state.IsFilterMode.ShouldBeFalse();
        state.FilterText.ShouldBe(string.Empty);
    }

    [Fact]
    public void ProcessKey_DownArrow_CursorChanges_ReturnsCursorMoved()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 0);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.CursorIndex.ShouldBe(1);
    }

    [Fact]
    public void ProcessKey_DownArrow_AtBottom_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 2);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    [Fact]
    public void ProcessKey_UpArrow_CursorChanges_ReturnsCursorMoved()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 2);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.CursorIndex.ShouldBe(1);
    }

    [Fact]
    public void ProcessKey_UpArrow_AtTop_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 0);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    [Fact]
    public void ProcessKey_LeftArrow_ReturnsCollapsed()
    {
        var state = CreateStateWithSiblings(3);
        state.Expand(new List<WorkItem> { CreateWorkItem(100, "Child") });
        var key = new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.Collapsed);
        state.Children.Count.ShouldBe(0);
    }

    [Fact]
    public void ProcessKey_RightArrow_NoChildren_ReturnsNeedExpand()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.NeedExpand);
    }

    [Fact]
    public void ProcessKey_RightArrow_HasChildren_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3);
        state.Expand(new List<WorkItem> { CreateWorkItem(100, "Child") });
        var key = new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    [Fact]
    public void ProcessKey_VimJ_MovesDown()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 0);
        var key = new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.CursorIndex.ShouldBe(1);
    }

    [Fact]
    public void ProcessKey_VimK_MovesUp()
    {
        var state = CreateStateWithSiblings(3, cursorIndex: 2);
        var key = new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.CursorIndex.ShouldBe(1);
    }

    [Fact]
    public void ProcessKey_Tab_ReturnsNone_Stubbed()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    [Fact]
    public void ProcessKey_UnknownKey_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('z', ConsoleKey.Z, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string RenderToString(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 120;
        console.Write(renderable);
        return console.Output;
    }

    private static TreeNavigatorState CreateStateWithSiblings(int count, int cursorIndex = 0)
    {
        var siblings = new List<WorkItem>();
        for (var i = 0; i < count; i++)
            siblings.Add(CreateWorkItem(i + 1, $"Item {i + 1}"));
        return CreateStateWithSiblings(siblings, cursorIndex);
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
