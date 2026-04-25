using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.TestKit;

public class SprintHierarchyBuilderTests
{
    [Fact]
    public void Build_WithSprintItemsAndParents_CreatesHierarchy()
    {
        var feature = new WorkItemBuilder(100, "Auth Feature").AsFeature().Build();
        var task1 = new WorkItemBuilder(1, "Login").AsTask().WithParent(100).AssignedTo("Alice").Build();
        var task2 = new WorkItemBuilder(2, "Logout").AsTask().WithParent(100).AssignedTo("Alice").Build();

        var hierarchy = new SprintHierarchyBuilder()
            .WithSprintItems(task1, task2)
            .WithParents(feature)
            .WithCeilingTypes("Feature")
            .Build();

        hierarchy.AssigneeGroups.ShouldContainKey("Alice");
        var roots = hierarchy.AssigneeGroups["Alice"];
        roots.Count.ShouldBe(1);
        roots[0].Item.Id.ShouldBe(100);
        roots[0].Children.Count.ShouldBe(2);
    }

    [Fact]
    public void Build_Empty_ReturnsEmptyHierarchy()
    {
        var hierarchy = new SprintHierarchyBuilder()
            .WithCeilingTypes("Feature")
            .Build();

        hierarchy.AssigneeGroups.ShouldBeEmpty();
    }

    [Fact]
    public void Build_NoCeilingTypes_FlatLayout()
    {
        var task1 = new WorkItemBuilder(1, "Task 1").AssignedTo("Bob").Build();
        var task2 = new WorkItemBuilder(2, "Task 2").AssignedTo("Bob").Build();

        var hierarchy = new SprintHierarchyBuilder()
            .WithSprintItems(task1, task2)
            .Build();

        var roots = hierarchy.AssigneeGroups["Bob"];
        roots.Count.ShouldBe(2);
        roots.ShouldAllBe(n => n.Children.Count == 0);
    }
}
