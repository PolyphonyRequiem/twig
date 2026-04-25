using Shouldly;
using Twig.Domain.ReadModels;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

public class WorkspaceSectionsTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Build — single Sprint section
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SprintItemsOnly_CreatesSingleSprintSection()
    {
        var items = new[]
        {
            WorkItemBuilder.Simple(1, "Task A"),
            WorkItemBuilder.Simple(2, "Task B"),
        };

        var sections = WorkspaceSections.Build(items);

        sections.Sections.Count.ShouldBe(1);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[0].Items.Count.ShouldBe(2);
        sections.ExcludedItemIds.ShouldBeEmpty();
    }

    [Fact]
    public void Build_EmptySprintItems_ReturnsNoSections()
    {
        var sections = WorkspaceSections.Build(Array.Empty<Domain.Aggregates.WorkItem>());

        sections.Sections.ShouldBeEmpty();
        sections.ExcludedItemIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Build — deduplication (first-mode-wins)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_DuplicateAcrossSprintAndArea_FirstModeWins()
    {
        var shared = WorkItemBuilder.Simple(1, "Shared Task");
        var areaOnly = WorkItemBuilder.Simple(2, "Area Only");

        var sections = WorkspaceSections.Build(
            sprintItems: new[] { shared },
            areaItems: new[] { shared, areaOnly });

        sections.Sections.Count.ShouldBe(2);

        // Sprint section has the shared item
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[0].Items.Count.ShouldBe(1);
        sections.Sections[0].Items[0].Id.ShouldBe(1);

        // Area section only has the unique item (shared was deduped)
        sections.Sections[1].ModeName.ShouldBe("Area");
        sections.Sections[1].Items.Count.ShouldBe(1);
        sections.Sections[1].Items[0].Id.ShouldBe(2);
    }

    [Fact]
    public void Build_DuplicateAcrossThreeModes_FirstModeWins()
    {
        var shared = WorkItemBuilder.Simple(1, "Shared");
        var sprintOnly = WorkItemBuilder.Simple(2, "Sprint Only");
        var recentOnly = WorkItemBuilder.Simple(3, "Recent Only");

        var sections = WorkspaceSections.Build(
            sprintItems: new[] { sprintOnly, shared },
            areaItems: new[] { shared },
            recentItems: new[] { shared, recentOnly });

        // Sprint has both items
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[0].Items.Count.ShouldBe(2);

        // Area section is empty after dedup — should be omitted
        sections.Sections.Count.ShouldBe(2);

        // Recent has only the unique item
        sections.Sections[1].ModeName.ShouldBe("Recent");
        sections.Sections[1].Items.Count.ShouldBe(1);
        sections.Sections[1].Items[0].Id.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Build — manual inclusions (no dedup)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ManualItemsAlwaysShown_EvenIfInSprint()
    {
        var shared = WorkItemBuilder.Simple(1, "Shared Task");
        var manualOnly = WorkItemBuilder.Simple(2, "Manual Only");

        var sections = WorkspaceSections.Build(
            sprintItems: new[] { shared },
            manualItems: new[] { shared, manualOnly });

        sections.Sections.Count.ShouldBe(2);

        // Sprint has the shared item
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[0].Items[0].Id.ShouldBe(1);

        // Manual section has BOTH items — no dedup for manual
        sections.Sections[1].ModeName.ShouldBe("Manual");
        sections.Sections[1].Items.Count.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Build — empty sections omitted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptyModesOmitted()
    {
        var item = WorkItemBuilder.Simple(1, "Task");

        var sections = WorkspaceSections.Build(
            sprintItems: new[] { item },
            areaItems: Array.Empty<Domain.Aggregates.WorkItem>(),
            recentItems: null);

        sections.Sections.Count.ShouldBe(1);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
    }

    [Fact]
    public void Build_SectionOmittedWhenAllItemsDeduped()
    {
        var item = WorkItemBuilder.Simple(1, "Only Item");

        var sections = WorkspaceSections.Build(
            sprintItems: new[] { item },
            areaItems: new[] { item }); // same item, will be deduped

        // Area section should be omitted since all items were deduped
        sections.Sections.Count.ShouldBe(1);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Build — exclusion footer
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_ExcludedIds_Preserved()
    {
        var sections = WorkspaceSections.Build(
            sprintItems: new[] { WorkItemBuilder.Simple(1, "Task") },
            excludedIds: new[] { 42, 99 });

        sections.ExcludedItemIds.Count.ShouldBe(2);
        sections.ExcludedItemIds.ShouldContain(42);
        sections.ExcludedItemIds.ShouldContain(99);
    }

    [Fact]
    public void Build_NoExcludedIds_EmptyList()
    {
        var sections = WorkspaceSections.Build(
            sprintItems: new[] { WorkItemBuilder.Simple(1, "Task") });

        sections.ExcludedItemIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Build — section ordering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SectionsInModeOrder()
    {
        var sections = WorkspaceSections.Build(
            sprintItems: new[] { WorkItemBuilder.Simple(1, "Sprint") },
            areaItems: new[] { WorkItemBuilder.Simple(2, "Area") },
            recentItems: new[] { WorkItemBuilder.Simple(3, "Recent") },
            manualItems: new[] { WorkItemBuilder.Simple(4, "Manual") });

        sections.Sections.Count.ShouldBe(4);
        sections.Sections[0].ModeName.ShouldBe("Sprint");
        sections.Sections[1].ModeName.ShouldBe("Area");
        sections.Sections[2].ModeName.ShouldBe("Recent");
        sections.Sections[3].ModeName.ShouldBe("Manual");
    }
}
