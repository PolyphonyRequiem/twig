using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class TreeNavigatorStateTests
{
    // ── MoveCursorUp ────────────────────────────────────────────────

    [Fact]
    public void MoveCursorUp_AtTopBoundary_IndexStaysZero()
    {
        var state = CreateState(siblingCount: 3, cursorIndex: 0);

        state.MoveCursorUp();

        state.CursorIndex.ShouldBe(0);
        state.CursorItem!.Id.ShouldBe(1);
    }

    [Fact]
    public void MoveCursorUp_FromMiddle_DecrementsCursorIndex()
    {
        var state = CreateState(siblingCount: 5, cursorIndex: 3);

        state.MoveCursorUp();

        state.CursorIndex.ShouldBe(2);
        state.CursorItem!.Id.ShouldBe(3);
    }

    [Fact]
    public void MoveCursorUp_FromEnd_MovesToPreviousItem()
    {
        var state = CreateState(siblingCount: 3, cursorIndex: 2);

        state.MoveCursorUp();

        state.CursorIndex.ShouldBe(1);
        state.CursorItem!.Id.ShouldBe(2);
    }

    [Fact]
    public void MoveCursorUp_EmptySiblings_NoOp()
    {
        var state = CreateState(siblingCount: 0);

        state.MoveCursorUp();

        state.CursorIndex.ShouldBe(0);
        state.CursorItem.ShouldBeNull();
    }

    // ── MoveCursorDown ──────────────────────────────────────────────

    [Fact]
    public void MoveCursorDown_AtBottomBoundary_IndexStaysAtEnd()
    {
        var state = CreateState(siblingCount: 3, cursorIndex: 2);

        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(2);
        state.CursorItem!.Id.ShouldBe(3);
    }

    [Fact]
    public void MoveCursorDown_FromMiddle_IncrementsCursorIndex()
    {
        var state = CreateState(siblingCount: 5, cursorIndex: 1);

        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(2);
        state.CursorItem!.Id.ShouldBe(3);
    }

    [Fact]
    public void MoveCursorDown_FromStart_MovesToNextItem()
    {
        var state = CreateState(siblingCount: 3, cursorIndex: 0);

        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(1);
        state.CursorItem!.Id.ShouldBe(2);
    }

    [Fact]
    public void MoveCursorDown_EmptySiblings_NoOp()
    {
        var state = CreateState(siblingCount: 0);

        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(0);
        state.CursorItem.ShouldBeNull();
    }

    // ── Combined Up/Down navigation ─────────────────────────────────

    [Fact]
    public void MoveCursor_UpThenDown_ReturnsToOriginalPosition()
    {
        var state = CreateState(siblingCount: 5, cursorIndex: 2);

        state.MoveCursorUp();
        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(2);
        state.CursorItem!.Id.ShouldBe(3);
    }

    // ── Expand / Collapse ───────────────────────────────────────────

    [Fact]
    public void Expand_SetsChildren()
    {
        var state = CreateState(siblingCount: 2);
        var children = CreateWorkItems(3, startId: 100);

        state.Expand(children);

        state.Children.Count.ShouldBe(3);
        state.Children[0].Id.ShouldBe(100);
        state.Children[1].Id.ShouldBe(101);
        state.Children[2].Id.ShouldBe(102);
    }

    [Fact]
    public void Collapse_ClearsChildren()
    {
        var state = CreateState(siblingCount: 2);
        state.Expand(CreateWorkItems(3, startId: 100));

        state.Collapse();

        state.Children.Count.ShouldBe(0);
    }

    [Fact]
    public void Expand_EmptyChildren_SetsEmptyList()
    {
        var state = CreateState(siblingCount: 2);

        state.Expand(Array.Empty<WorkItem>());

        state.Children.Count.ShouldBe(0);
    }

    // ── ApplyFilter ─────────────────────────────────────────────────

    [Fact]
    public void ApplyFilter_NarrowsVisibleSiblings()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha task"),
            CreateWorkItem(2, "Beta feature"),
            CreateWorkItem(3, "Alpha bug"),
            CreateWorkItem(4, "Gamma item"),
        };
        var state = CreateStateWithSiblings(siblings, cursorIndex: 2);

        state.ApplyFilter("Alpha");

        state.VisibleSiblings.Count.ShouldBe(2);
        state.VisibleSiblings[0].Title.ShouldBe("Alpha task");
        state.VisibleSiblings[1].Title.ShouldBe("Alpha bug");
        state.CursorIndex.ShouldBe(0);
        state.IsFilterMode.ShouldBeTrue();
        state.FilterText.ShouldBe("Alpha");
    }

    [Fact]
    public void ApplyFilter_CaseInsensitive()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "UPPERCASE Title"),
            CreateWorkItem(2, "lowercase title"),
            CreateWorkItem(3, "No match"),
        };
        var state = CreateStateWithSiblings(siblings);

        state.ApplyFilter("title");

        state.VisibleSiblings.Count.ShouldBe(2);
    }

    [Fact]
    public void ApplyFilter_NoMatch_EmptySiblings()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha"),
            CreateWorkItem(2, "Beta"),
        };
        var state = CreateStateWithSiblings(siblings);

        state.ApplyFilter("Zulu");

        state.VisibleSiblings.Count.ShouldBe(0);
        state.CursorItem.ShouldBeNull();
    }

    [Fact]
    public void ApplyFilter_ResetsCursorToZero()
    {
        var state = CreateState(siblingCount: 5, cursorIndex: 4);

        state.ApplyFilter("Item");

        state.CursorIndex.ShouldBe(0);
    }

    // ── ClearFilter ─────────────────────────────────────────────────

    [Fact]
    public void ClearFilter_RestoresAllSiblings()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha"),
            CreateWorkItem(2, "Beta"),
            CreateWorkItem(3, "Gamma"),
        };
        var state = CreateStateWithSiblings(siblings, cursorIndex: 2);
        state.ApplyFilter("Alpha");
        state.VisibleSiblings.Count.ShouldBe(1);

        state.ClearFilter();

        state.VisibleSiblings.Count.ShouldBe(3);
        state.IsFilterMode.ShouldBeFalse();
        state.FilterText.ShouldBe(string.Empty);
        state.CursorIndex.ShouldBe(2);
        state.CursorItem!.Id.ShouldBe(3);
    }

    [Fact]
    public void ClearFilter_RestoresPreFilterCursorIndex()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha task"),
            CreateWorkItem(2, "Beta feature"),
            CreateWorkItem(3, "Alpha bug"),
            CreateWorkItem(4, "Gamma item"),
            CreateWorkItem(5, "Delta item"),
        };
        var state = CreateStateWithSiblings(siblings, cursorIndex: 3);
        state.CursorItem!.Id.ShouldBe(4);

        state.ApplyFilter("Alpha");
        state.CursorIndex.ShouldBe(0);

        state.ClearFilter();

        state.CursorIndex.ShouldBe(3);
        state.CursorItem!.Id.ShouldBe(4);
    }

    // ── GetCombinedLinks ────────────────────────────────────────────

    [Fact]
    public void GetCombinedLinks_MergesLinksAndSeedLinks()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
            new(SourceId: 1, TargetId: 20, LinkType: "Predecessor"),
        };
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -5, LinkType: "blocks", CreatedAt: DateTimeOffset.UtcNow),
        };
        var state = new TreeNavigatorState(
            CreateWorkItem(1, "Test"),
            Array.Empty<WorkItem>(),
            new List<WorkItem> { CreateWorkItem(1, "Test") },
            Array.Empty<WorkItem>(),
            links,
            seedLinks);

        var combined = state.GetCombinedLinks();

        combined.Count.ShouldBe(3);
        combined[0].ShouldBe(new NavigatorLink(10, "Related", IsSeed: false));
        combined[1].ShouldBe(new NavigatorLink(20, "Predecessor", IsSeed: false));
        combined[2].ShouldBe(new NavigatorLink(-5, "blocks", IsSeed: true));
    }

    [Fact]
    public void GetCombinedLinks_EmptyLists_ReturnsEmpty()
    {
        var state = CreateState(siblingCount: 1);

        var combined = state.GetCombinedLinks();

        combined.Count.ShouldBe(0);
    }

    [Fact]
    public void GetCombinedLinks_OnlyWorkItemLinks_AllHaveIsSeedFalse()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 42, LinkType: "Successor"),
        };
        var state = new TreeNavigatorState(
            CreateWorkItem(1, "Test"),
            Array.Empty<WorkItem>(),
            new List<WorkItem> { CreateWorkItem(1, "Test") },
            Array.Empty<WorkItem>(),
            links,
            Array.Empty<SeedLink>());

        var combined = state.GetCombinedLinks();

        combined.Count.ShouldBe(1);
        combined[0].IsSeed.ShouldBeFalse();
    }

    [Fact]
    public void GetCombinedLinks_OnlySeedLinks_AllHaveIsSeedTrue()
    {
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -1, LinkType: "related", CreatedAt: DateTimeOffset.UtcNow),
        };
        var state = new TreeNavigatorState(
            CreateWorkItem(1, "Test"),
            Array.Empty<WorkItem>(),
            new List<WorkItem> { CreateWorkItem(1, "Test") },
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            seedLinks);

        var combined = state.GetCombinedLinks();

        combined.Count.ShouldBe(1);
        combined[0].IsSeed.ShouldBeTrue();
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Fact]
    public void Constructor_SingleSibling_CursorAtZero()
    {
        var state = CreateState(siblingCount: 1);

        state.CursorIndex.ShouldBe(0);
        state.CursorItem!.Id.ShouldBe(1);
    }

    [Fact]
    public void Constructor_CursorItemInSiblings_IndexMatchesPosition()
    {
        var siblings = CreateWorkItems(4);
        var cursorItem = siblings[2]; // Id=3, index=2
        var state = new TreeNavigatorState(
            cursorItem,
            Array.Empty<WorkItem>(),
            siblings,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        state.CursorIndex.ShouldBe(2);
    }

    [Fact]
    public void MoveCursorDown_SingleChild_NoMovement()
    {
        var state = CreateState(siblingCount: 1);

        state.MoveCursorDown();

        state.CursorIndex.ShouldBe(0);
    }

    // ── AcceptFilter ────────────────────────────────────────────────

    [Fact]
    public void AcceptFilter_KeepsCursorOnSelectedItem()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha task"),
            CreateWorkItem(2, "Beta feature"),
            CreateWorkItem(3, "Alpha bug"),
            CreateWorkItem(4, "Gamma item"),
        };
        var state = CreateStateWithSiblings(siblings, cursorIndex: 0);
        state.ApplyFilter("Alpha");
        state.VisibleSiblings.Count.ShouldBe(2);
        // Move cursor to second filtered result (Alpha bug, id=3)
        state.MoveCursorDown();
        state.CursorItem!.Id.ShouldBe(3);

        state.AcceptFilter();

        state.IsFilterMode.ShouldBeFalse();
        state.FilterText.ShouldBe(string.Empty);
        state.VisibleSiblings.Count.ShouldBe(4);
        state.CursorItem!.Id.ShouldBe(3);
        state.CursorIndex.ShouldBe(2); // Position of id=3 in full siblings
    }

    [Fact]
    public void AcceptFilter_NullCursorItem_ResetsCursorToFirst()
    {
        var siblings = new List<WorkItem>
        {
            CreateWorkItem(1, "Alpha"),
            CreateWorkItem(2, "Beta"),
        };
        var state = CreateStateWithSiblings(siblings);
        state.ApplyFilter("Zulu"); // No matches
        state.CursorItem.ShouldBeNull();

        state.AcceptFilter();

        state.IsFilterMode.ShouldBeFalse();
        state.VisibleSiblings.Count.ShouldBe(2);
        state.CursorIndex.ShouldBe(0);
        state.CursorItem!.Id.ShouldBe(1);
    }

    [Fact]
    public void AcceptFilter_EmptySiblings_NullCursorItem()
    {
        var state = CreateState(siblingCount: 0);
        // Manually set filter mode for empty list
        state.ApplyFilter("anything");

        state.AcceptFilter();

        state.IsFilterMode.ShouldBeFalse();
        state.CursorItem.ShouldBeNull();
    }

    // ── AdvanceLinkJump ─────────────────────────────────────────────

    [Fact]
    public void AdvanceLinkJump_CyclesThroughLinks()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
            new(SourceId: 1, TargetId: 20, LinkType: "Predecessor"),
        };
        var state = CreateStateWithLinks(links, Array.Empty<SeedLink>());

        var link1 = state.AdvanceLinkJump();
        link1.ShouldNotBeNull();
        link1.Value.TargetId.ShouldBe(10);
        state.LinkJumpIndex.ShouldBe(0);

        var link2 = state.AdvanceLinkJump();
        link2.ShouldNotBeNull();
        link2.Value.TargetId.ShouldBe(20);
        state.LinkJumpIndex.ShouldBe(1);
    }

    [Fact]
    public void AdvanceLinkJump_WrapsAround()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
            new(SourceId: 1, TargetId: 20, LinkType: "Predecessor"),
        };
        var state = CreateStateWithLinks(links, Array.Empty<SeedLink>());

        state.AdvanceLinkJump(); // index 0
        state.AdvanceLinkJump(); // index 1

        var wrapped = state.AdvanceLinkJump(); // wraps to 0
        wrapped.ShouldNotBeNull();
        wrapped.Value.TargetId.ShouldBe(10);
        state.LinkJumpIndex.ShouldBe(0);
    }

    [Fact]
    public void AdvanceLinkJump_EmptyLinks_ReturnsNull()
    {
        var state = CreateState(siblingCount: 1);

        var result = state.AdvanceLinkJump();

        result.ShouldBeNull();
    }

    [Fact]
    public void AdvanceLinkJump_IncludesSeedLinks()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
        };
        var seedLinks = new List<SeedLink>
        {
            new(SourceId: 1, TargetId: -5, LinkType: "blocks", CreatedAt: DateTimeOffset.UtcNow),
        };
        var state = CreateStateWithLinks(links, seedLinks);

        state.AdvanceLinkJump(); // index 0 → WorkItemLink
        var seedLink = state.AdvanceLinkJump(); // index 1 → SeedLink

        seedLink.ShouldNotBeNull();
        seedLink.Value.TargetId.ShouldBe(-5);
        seedLink.Value.IsSeed.ShouldBeTrue();
    }

    // ── ReverseLinkJump ─────────────────────────────────────────────

    [Fact]
    public void ReverseLinkJump_CyclesBackward()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
            new(SourceId: 1, TargetId: 20, LinkType: "Predecessor"),
            new(SourceId: 1, TargetId: 30, LinkType: "Successor"),
        };
        var state = CreateStateWithLinks(links, Array.Empty<SeedLink>());

        // First reverse from -1 → wraps to last (index 2)
        var last = state.ReverseLinkJump();
        last.ShouldNotBeNull();
        last.Value.TargetId.ShouldBe(30);
        state.LinkJumpIndex.ShouldBe(2);

        var prev = state.ReverseLinkJump();
        prev.ShouldNotBeNull();
        prev.Value.TargetId.ShouldBe(20);
        state.LinkJumpIndex.ShouldBe(1);
    }

    [Fact]
    public void ReverseLinkJump_EmptyLinks_ReturnsNull()
    {
        var state = CreateState(siblingCount: 1);

        var result = state.ReverseLinkJump();

        result.ShouldBeNull();
    }

    [Fact]
    public void ReverseLinkJump_WrapsFromZeroToLast()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
            new(SourceId: 1, TargetId: 20, LinkType: "Predecessor"),
        };
        var state = CreateStateWithLinks(links, Array.Empty<SeedLink>());
        state.AdvanceLinkJump(); // index 0

        var wrapped = state.ReverseLinkJump(); // 0 → wraps to 1
        wrapped.ShouldNotBeNull();
        wrapped.Value.TargetId.ShouldBe(20);
        state.LinkJumpIndex.ShouldBe(1);
    }

    // ── UpdateNodeData resets LinkJumpIndex ──────────────────────────

    [Fact]
    public void UpdateNodeData_ResetsLinkJumpIndex()
    {
        var links = new List<WorkItemLink>
        {
            new(SourceId: 1, TargetId: 10, LinkType: "Related"),
        };
        var state = CreateStateWithLinks(links, Array.Empty<SeedLink>());
        state.AdvanceLinkJump();
        state.LinkJumpIndex.ShouldBe(0);

        state.UpdateNodeData(
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItemLink>(),
            Array.Empty<SeedLink>());

        state.LinkJumpIndex.ShouldBe(-1);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static TreeNavigatorState CreateState(int siblingCount, int cursorIndex = 0)
    {
        var siblings = CreateWorkItems(siblingCount);
        var cursorItem = siblingCount > 0 && cursorIndex < siblingCount
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

    private static List<WorkItem> CreateWorkItems(int count, int startId = 1)
    {
        var items = new List<WorkItem>();
        for (var i = 0; i < count; i++)
        {
            items.Add(CreateWorkItem(startId + i, $"Item {startId + i}"));
        }
        return items;
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static TreeNavigatorState CreateStateWithLinks(
        IReadOnlyList<WorkItemLink> links,
        IReadOnlyList<SeedLink> seedLinks)
    {
        var item = CreateWorkItem(1, "Test Item");
        return new TreeNavigatorState(
            item,
            Array.Empty<WorkItem>(),
            new List<WorkItem> { item },
            Array.Empty<WorkItem>(),
            links,
            seedLinks);
    }
}
