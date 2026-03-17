using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

public class WorkspaceTests
{
    // ═══════════════════════════════════════════════════════════════
    //  ListAll — deduplication
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ListAll_DeduplicatesById()
    {
        var item = MakeItem(1, "Shared item");
        var seed = MakeSeed("Seed A", daysOld: 1);

        // Same item appears in both sprint and context
        var ws = Workspace.Build(item, new[] { item }, new[] { seed });

        var all = ws.ListAll();

        all.Count.ShouldBe(2); // item + seed (deduplicated)
    }

    [Fact]
    public void ListAll_SeedsAlwaysIncluded()
    {
        var seed1 = MakeSeed("Seed 1", daysOld: 1);
        var seed2 = MakeSeed("Seed 2", daysOld: 2);

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed1, seed2 });

        var all = ws.ListAll();

        all.Count.ShouldBe(2);
    }

    [Fact]
    public void ListAll_CombinesAll()
    {
        var context = MakeItem(1, "Context");
        var sprint1 = MakeItem(2, "Sprint item 1");
        var sprint2 = MakeItem(3, "Sprint item 2");
        var seed = MakeSeed("Seed", daysOld: 1);

        var ws = Workspace.Build(context, new[] { sprint1, sprint2 }, new[] { seed });

        var all = ws.ListAll();

        all.Count.ShouldBe(4); // context + 2 sprint + 1 seed
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetStaleSeeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetStaleSeeds_FiltersCorrectly()
    {
        var stale = MakeSeed("Old seed", daysOld: 10);
        var fresh = MakeSeed("New seed", daysOld: 1);

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { stale, fresh });

        var result = ws.GetStaleSeeds(5);

        result.Count.ShouldBe(1);
        result[0].Title.ShouldBe("Old seed");
    }

    [Fact]
    public void GetStaleSeeds_AllStale()
    {
        var seed1 = MakeSeed("Seed 1", daysOld: 30);
        var seed2 = MakeSeed("Seed 2", daysOld: 15);

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed1, seed2 });

        var result = ws.GetStaleSeeds(7);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void GetStaleSeeds_NoneStale()
    {
        var seed = MakeSeed("Fresh seed", daysOld: 1);

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        var result = ws.GetStaleSeeds(7);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetStaleSeeds_MixedStaleFresh()
    {
        var stale1 = MakeSeed("Stale 1", daysOld: 20);
        var fresh1 = MakeSeed("Fresh 1", daysOld: 2);
        var stale2 = MakeSeed("Stale 2", daysOld: 8);
        var fresh2 = MakeSeed("Fresh 2", daysOld: 0);

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { stale1, fresh1, stale2, fresh2 });

        var result = ws.GetStaleSeeds(7);

        result.Count.ShouldBe(2);
        result[0].Title.ShouldBe("Stale 1");
        result[1].Title.ShouldBe("Stale 2");
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetDirtyItems
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetDirtyItems_FiltersCorrectly()
    {
        var dirty = MakeItem(1, "Dirty item");
        dirty.ChangeState("Active");

        var clean = MakeItem(2, "Clean item");

        var ws = Workspace.Build(null, new[] { dirty, clean }, Array.Empty<WorkItem>());

        var result = ws.GetDirtyItems();

        result.Count.ShouldBe(1);
        result[0].Title.ShouldBe("Dirty item");
    }

    [Fact]
    public void GetDirtyItems_IncludesDirtySeeds()
    {
        var dirtySeed = WorkItem.CreateSeed(WorkItemType.Task, "Dirty seed");
        dirtySeed.ChangeState("Active");

        var cleanSprint = MakeItem(2, "Clean sprint");

        var ws = Workspace.Build(null, new[] { cleanSprint }, new[] { dirtySeed });

        var result = ws.GetDirtyItems();

        result.Count.ShouldBe(1);
        result[0].Title.ShouldBe("Dirty seed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Null context
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_NullContext_IsValid()
    {
        var seed = MakeSeed("Seed", daysOld: 1);
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        ws.ContextItem.ShouldBeNull();
        ws.ListAll().Count.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty sprint/seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_EmptySprintAndSeeds()
    {
        var context = MakeItem(1, "Context");
        var ws = Workspace.Build(context, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.ListAll().Count.ShouldBe(1); // just context
        ws.GetDirtyItems().ShouldBeEmpty();
        ws.GetStaleSeeds(7).ShouldBeEmpty();
    }

    [Fact]
    public void Build_AllEmpty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.ListAll().ShouldBeEmpty();
        ws.GetDirtyItems().ShouldBeEmpty();
        ws.GetStaleSeeds(7).ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GetStaleSeeds_NullSeedCreatedAt_IsExcluded()
    {
        var seed = new WorkItem
        {
            Id = -100,
            Type = WorkItemType.Task,
            Title = "Seed with null date",
            IsSeed = true,
            SeedCreatedAt = null,
        };

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        ws.GetStaleSeeds(7).ShouldBeEmpty();
    }

    [Fact]
    public void ListAll_MultipleSeedsFromCreateSeed_AllIncluded()
    {
        var seed1 = WorkItem.CreateSeed(WorkItemType.Task, "Seed A");
        var seed2 = WorkItem.CreateSeed(WorkItemType.Task, "Seed B");

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed1, seed2 });

        var all = ws.ListAll();

        all.Count.ShouldBe(2);
        all.ShouldContain(s => s.Title == "Seed A");
        all.ShouldContain(s => s.Title == "Seed B");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Hierarchy property
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_WithHierarchy_ExposesHierarchy()
    {
        var item = MakeItem(1, "Sprint item");
        var hierarchy = SprintHierarchy.Build(
            new[] { item },
            new Dictionary<int, WorkItem>(),
            null);

        var ws = Workspace.Build(null, new[] { item }, Array.Empty<WorkItem>(), hierarchy);

        ws.Hierarchy.ShouldNotBeNull();
        ws.Hierarchy.ShouldBeSameAs(hierarchy);
    }

    [Fact]
    public void Build_WithoutHierarchy_HierarchyIsNull()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.Hierarchy.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static int _seedCounter = -1;

    private static WorkItem MakeItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
        };
    }

    private static WorkItem MakeSeed(string title, int daysOld)
    {
        return new WorkItem
        {
            Id = Interlocked.Decrement(ref _seedCounter),
            Type = WorkItemType.Task,
            Title = title,
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-daysOld),
        };
    }
}
