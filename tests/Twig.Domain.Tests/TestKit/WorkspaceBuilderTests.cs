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
        var hierarchy = new SprintHierarchyBuilder()
            .WithSprintItems(task)
            .Build();

        var ws = new WorkspaceBuilder()
            .WithSprintItems(task)
            .WithHierarchy(hierarchy)
            .Build();

        ws.Hierarchy.ShouldNotBeNull();
    }
}
