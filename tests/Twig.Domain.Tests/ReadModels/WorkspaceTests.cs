using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
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
        var item = WorkItemBuilder.Simple(1, "Shared item");
        var seed = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed A").AsSeed(1).Build();

        // Same item appears in both sprint and context
        var ws = Workspace.Build(item, new[] { item }, new[] { seed });

        var all = ws.ListAll();

        all.Count.ShouldBe(2); // item + seed (deduplicated)
    }

    [Fact]
    public void ListAll_SeedsAlwaysIncluded()
    {
        var seed1 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed 1").AsSeed(1).Build();
        var seed2 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed 2").AsSeed(2).Build();

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed1, seed2 });

        var all = ws.ListAll();

        all.Count.ShouldBe(2);
    }

    [Fact]
    public void ListAll_CombinesAll()
    {
        var context = WorkItemBuilder.Simple(1, "Context");
        var sprint1 = WorkItemBuilder.Simple(2, "Sprint item 1");
        var sprint2 = WorkItemBuilder.Simple(3, "Sprint item 2");
        var seed = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed").AsSeed(1).Build();

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
        var stale = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Old seed").AsSeed(10).Build();
        var fresh = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "New seed").AsSeed(1).Build();

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { stale, fresh });

        var result = ws.GetStaleSeeds(5);

        result.Count.ShouldBe(1);
        result[0].Title.ShouldBe("Old seed");
    }

    [Fact]
    public void GetStaleSeeds_AllStale()
    {
        var seed1 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed 1").AsSeed(30).Build();
        var seed2 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed 2").AsSeed(15).Build();

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed1, seed2 });

        var result = ws.GetStaleSeeds(7);

        result.Count.ShouldBe(2);
    }

    [Fact]
    public void GetStaleSeeds_NoneStale()
    {
        var seed = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Fresh seed").AsSeed(1).Build();

        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { seed });

        var result = ws.GetStaleSeeds(7);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetStaleSeeds_MixedStaleFresh()
    {
        var stale1 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Stale 1").AsSeed(20).Build();
        var fresh1 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Fresh 1").AsSeed(2).Build();
        var stale2 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Stale 2").AsSeed(8).Build();
        var fresh2 = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Fresh 2").AsSeed(0).Build();

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
        var dirty = WorkItemBuilder.Simple(1, "Dirty item");
        dirty.ChangeState("Active");

        var clean = WorkItemBuilder.Simple(2, "Clean item");

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

        var cleanSprint = WorkItemBuilder.Simple(2, "Clean sprint");

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
        var seed = new WorkItemBuilder(Interlocked.Decrement(ref _seedCounter), "Seed").AsSeed(1).Build();
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
        var context = WorkItemBuilder.Simple(1, "Context");
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
        var item = WorkItemBuilder.Simple(1, "Sprint item");
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
    //  TrackedItems + ExcludedIds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_WithTrackedItems_ExposesTrackedItems()
    {
        var tracked = new TrackedItem(42, Domain.Enums.TrackingMode.Single, DateTimeOffset.UtcNow);
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            trackedItems: new[] { tracked });

        ws.TrackedItems.Count.ShouldBe(1);
        ws.TrackedItems[0].WorkItemId.ShouldBe(42);
    }

    [Fact]
    public void Build_WithExcludedIds_ExposesExcludedIds()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            excludedIds: new[] { 10, 20 });

        ws.ExcludedIds.Count.ShouldBe(2);
        ws.ExcludedIds[0].ShouldBe(10);
        ws.ExcludedIds[1].ShouldBe(20);
    }

    [Fact]
    public void Build_DefaultTrackedItemsIsEmpty()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.TrackedItems.ShouldBeEmpty();
        ws.ExcludedIds.ShouldBeEmpty();
    }

    [Fact]
    public void IsTracked_ReturnsTrueForTrackedItem()
    {
        var tracked = new TrackedItem(42, Domain.Enums.TrackingMode.Single, DateTimeOffset.UtcNow);
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            trackedItems: new[] { tracked });

        ws.IsTracked(42).ShouldBeTrue();
    }

    [Fact]
    public void IsTracked_ReturnsFalseForUntrackedItem()
    {
        var tracked = new TrackedItem(42, Domain.Enums.TrackingMode.Single, DateTimeOffset.UtcNow);
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>(),
            trackedItems: new[] { tracked });

        ws.IsTracked(99).ShouldBeFalse();
    }

    [Fact]
    public void IsTracked_EmptyTrackedItems_ReturnsFalse()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.IsTracked(42).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static int _seedCounter = -1;
}
