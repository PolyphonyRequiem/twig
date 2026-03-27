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
    public void ProcessKey_Tab_NoLinks_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
    }

    [Fact]
    public void ProcessKey_Tab_WithLinks_ReturnsLinkJump()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
        };
        var state = new TreeNavigatorState(
            item, Array.Empty<WorkItem>(), new List<WorkItem> { item },
            Array.Empty<WorkItem>(), links, Array.Empty<SeedLink>());
        var key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.LinkJump);
        state.LinkJumpIndex.ShouldBe(0);
    }

    [Fact]
    public void ProcessKey_ShiftTab_WithLinks_ReturnsLinkJump()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
            new(SourceId: 1, TargetId: 99, LinkType: "Predecessor"),
        };
        var state = new TreeNavigatorState(
            item, Array.Empty<WorkItem>(), new List<WorkItem> { item },
            Array.Empty<WorkItem>(), links, Array.Empty<SeedLink>());
        var key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.LinkJump);
        // Shift+Tab from -1 wraps to last link (index 1)
        state.LinkJumpIndex.ShouldBe(1);
    }

    // ── ProcessKey — Filter mode (ITEM-012) ────────────────────────

    [Fact]
    public void ProcessKey_AlphanumericKey_EntersFilterMode()
    {
        var state = CreateStateWithSiblings(3);
        state.IsFilterMode.ShouldBeFalse();
        var key = new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterUpdated);
        state.IsFilterMode.ShouldBeTrue();
        state.FilterText.ShouldBe("a");
    }

    [Fact]
    public void ProcessKey_DigitKey_EntersFilterMode()
    {
        var state = CreateStateWithSiblings(3);
        var key = new ConsoleKeyInfo('5', ConsoleKey.D5, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterUpdated);
        state.FilterText.ShouldBe("5");
    }

    [Fact]
    public void ProcessKey_FilterMode_AppendCharacter()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("te");
        var key = new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterUpdated);
        state.FilterText.ShouldBe("tes");
    }

    [Fact]
    public void ProcessKey_FilterMode_Backspace_RemovesLastChar()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("abc");
        var key = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterUpdated);
        state.FilterText.ShouldBe("ab");
    }

    [Fact]
    public void ProcessKey_FilterMode_Backspace_LastChar_ExitsFilterMode()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("x");
        var key = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterCleared);
        state.IsFilterMode.ShouldBeFalse();
        state.FilterText.ShouldBe(string.Empty);
    }

    [Fact]
    public void ProcessKey_FilterMode_Backspace_EmptyText_ExitsFilterMode()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("");
        state.IsFilterMode.ShouldBeTrue();
        var key = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterCleared);
        state.IsFilterMode.ShouldBeFalse();
    }

    [Fact]
    public void ProcessKey_FilterMode_Enter_AcceptsFilter()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha task"),
            CreateWorkItem(2, "Beta feature"),
            CreateWorkItem(3, "Alpha bug"),
        };
        var state = CreateStateWithSiblings(siblings);
        state.ApplyFilter("Alpha");
        state.VisibleSiblings.Count.ShouldBe(2);
        var key = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.IsFilterMode.ShouldBeFalse();
        state.VisibleSiblings.Count.ShouldBe(3); // All siblings restored
        state.CursorItem!.Title.ShouldBe("Alpha task"); // Kept cursor on filtered selection
    }

    [Fact]
    public void ProcessKey_FilterMode_UpDown_NavigatesWithinFilteredResults()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha task"),
            CreateWorkItem(2, "Beta feature"),
            CreateWorkItem(3, "Alpha bug"),
        };
        var state = CreateStateWithSiblings(siblings);
        state.ApplyFilter("Alpha");
        state.CursorIndex.ShouldBe(0);

        var downKey = new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false);
        var action = SpectreRenderer.ProcessKey(downKey, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.CursorMoved);
        state.CursorIndex.ShouldBe(1);
        state.CursorItem!.Title.ShouldBe("Alpha bug");
    }

    [Fact]
    public void ProcessKey_FilterMode_VimJ_AppendsToFilter()
    {
        var state = CreateStateWithSiblings(3);
        state.ApplyFilter("item");
        var key = new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.FilterUpdated);
        state.FilterText.ShouldBe("itemj");
    }

    [Fact]
    public void ProcessKey_FilterMode_Tab_ReturnsNone()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
        };
        var state = new TreeNavigatorState(
            item, Array.Empty<WorkItem>(), new List<WorkItem> { item },
            Array.Empty<WorkItem>(), links, Array.Empty<SeedLink>());
        state.ApplyFilter("Te");
        state.IsFilterMode.ShouldBeTrue();
        var key = new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);

        var action = SpectreRenderer.ProcessKey(key, state);

        action.ShouldBe(SpectreRenderer.NavigatorAction.None);
        state.LinkJumpIndex.ShouldBe(-1);
    }

    // ── BuildPreviewPanel — Link highlighting (ITEM-013) ────────────

    [Fact]
    public void BuildPreviewPanel_LinkHighlight_HighlightsCorrectLink()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
            new(SourceId: 1, TargetId: 99, LinkType: "Predecessor"),
        };

        // Render with ANSI sequences to detect aqua color highlighting
        var panel = SpectreRenderer.BuildPreviewPanel(
            item, links, Array.Empty<SeedLink>(), _theme, linkJumpIndex: 0);
        var output = RenderToAnsiString(panel);

        // Both link IDs should appear
        output.ShouldContain("#42");
        output.ShouldContain("#99");

        // Aqua ANSI escape should appear near the highlighted link (Related: #42)
        var relatedPos = output.IndexOf("Related");
        // Predecessor (#99) is NOT highlighted — no aqua code should bleed into it.
        // "38;5;14" is the ANSI SGR code for aqua (bright cyan)
        var aquaPositions = AllIndexesOf(output, "38;5;14");
        aquaPositions.ShouldNotBeEmpty("Expected aqua ANSI code for highlighted link");
        // At least one aqua code should appear before the highlighted link (Related)
        aquaPositions.ShouldContain(pos => pos < relatedPos,
            "Aqua highlight should appear before the highlighted link 'Related'");
        // No aqua code should appear after the Related link highlight region
        aquaPositions.ShouldNotContain(pos => pos > relatedPos,
            "Aqua highlight should NOT extend beyond the highlighted 'Related' link");
    }

    [Fact]
    public void BuildPreviewPanel_NoLinkHighlight_DefaultBehavior()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
        };

        var panel = SpectreRenderer.BuildPreviewPanel(item, links, Array.Empty<SeedLink>(), _theme);

        var output = RenderToString(panel);
        output.ShouldContain("#42");
        output.ShouldContain("Related");
    }

    [Fact]
    public void BuildPreviewPanel_SeedLinkHighlight_HighlightsSeedLink()
    {
        var item = CreateWorkItem(1, "Test");
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Related"),
        };
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -5, LinkType: "blocks", CreatedAt: DateTimeOffset.UtcNow),
        };

        // linkJumpIndex=1 → points to the seed link (index 0 in seedLinks, combined index 1)
        var panel = SpectreRenderer.BuildPreviewPanel(item, links, seedLinks, _theme, linkJumpIndex: 1);

        var output = RenderToString(panel);
        output.ShouldContain("#-5");
        output.ShouldContain("blocks");
    }

    [Fact]
    public void ProcessKey_NonPrintableKey_ReturnsNone()
    {
        var state = CreateStateWithSiblings(3);
        // F1 is non-printable, should be ignored
        var key = new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, false);

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

    private static string RenderToAnsiString(IRenderable renderable)
    {
        var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Width = 120;
        console.Write(renderable);
        return console.Output;
    }

    private static List<int> AllIndexesOf(string source, string value)
    {
        var indexes = new List<int>();
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) != -1)
        {
            indexes.Add(index);
            index += value.Length;
        }
        return indexes;
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
