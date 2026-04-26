using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.TestKit;

public class WorkspaceBuilderTests
{
    [Fact]
    public void Build_WithAllParts_CreatesWorkspace()
    {
        var context = new WorkItemBuilder(1, "Context item").Build();
        var sprint1 = new WorkItemBuilder(2, "Sprint item").Build();
        var seed = new WorkItemBuilder(-1, "Seed").AsSeed(1).Build();

        var ws = new WorkspaceBuilder()
            .WithContext(context)
            .WithSprintItems(sprint1)
            .WithSeeds(seed)
            .Build();

        ws.ContextItem.ShouldNotBeNull();
        ws.ContextItem!.Id.ShouldBe(1);
        ws.SprintItems.Count.ShouldBe(1);
        ws.Seeds.Count.ShouldBe(1);
    }

    [Fact]
    public void Build_Empty_CreatesEmptyWorkspace()
    {
        var ws = new WorkspaceBuilder().Build();

        ws.ContextItem.ShouldBeNull();
        ws.SprintItems.ShouldBeEmpty();
        ws.Seeds.ShouldBeEmpty();
        ws.Hierarchy.ShouldBeNull();
    }

    [Fact]
    public void Build_WithHierarchy_ExposesHierarchy()
    {
        var task = new WorkItemBuilder(1, "Task").AssignedTo("Alice").Build();
        var hierarchy = new SprintHierarchyTestBuilder()
            .WithSprintItems(task)
            .Build();

        var ws = new WorkspaceBuilder()
            .WithSprintItems(task)
            .WithHierarchy(hierarchy)
            .Build();

        ws.Hierarchy.ShouldNotBeNull();
    }

    [Fact]
    public void Build_WithTrackedItems_ExposesTrackedItems()
    {
        var tracked = new TrackedItem(42, Domain.Enums.TrackingMode.Tree, DateTimeOffset.UtcNow);

        var ws = new WorkspaceBuilder()
            .WithTrackedItems(tracked)
            .Build();

        ws.TrackedItems.Count.ShouldBe(1);
        ws.TrackedItems[0].WorkItemId.ShouldBe(42);
        ws.TrackedItems[0].Mode.ShouldBe(Domain.Enums.TrackingMode.Tree);
    }

    [Fact]
    public void Build_WithExcludedIds_ExposesExcludedIds()
    {
        var ws = new WorkspaceBuilder()
            .WithExcludedIds(10, 20, 30)
            .Build();

        ws.ExcludedIds.Count.ShouldBe(3);
        ws.ExcludedIds[0].ShouldBe(10);
    }

    [Fact]
    public void Build_Empty_TrackedAndExcludedAreEmpty()
    {
        var ws = new WorkspaceBuilder().Build();

        ws.TrackedItems.ShouldBeEmpty();
        ws.ExcludedIds.ShouldBeEmpty();
    }
}
