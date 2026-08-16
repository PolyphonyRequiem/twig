using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

/// <summary>
/// Tests for <see cref="WorkItemGraph"/>, the graph read model added by ADO #154:
/// a SET of work items and the edges among them.
/// </summary>
public class WorkItemGraphTests
{
    private static WorkItem Item(int id) => new WorkItemBuilder(id, $"Item {id}").Build();

    [Fact]
    public void Build_GroupsEdgesBySourceId()
    {
        var items = new[] { Item(1), Item(2) };
        var links = new[]
        {
            new WorkItemLink(1, 10, LinkTypes.Predecessor),
            new WorkItemLink(1, 11, LinkTypes.Successor),
            new WorkItemLink(2, 12, LinkTypes.Related),
        };

        var graph = WorkItemGraph.Build(items, links);

        // Each member gets ITS OWN edges — a mutant returning the whole edge set per item
        // would give item 1 three links instead of two.
        graph.GetLinks(1).Count.ShouldBe(2);
        graph.GetLinks(1).ShouldContain(l => l.TargetId == 10);
        graph.GetLinks(1).ShouldContain(l => l.TargetId == 11);

        graph.GetLinks(2).Count.ShouldBe(1);
        graph.GetLinks(2)[0].TargetId.ShouldBe(12);
    }

    [Fact]
    public void GetLinks_ItemWithNoEdges_ReturnsEmptyNotNull()
    {
        var graph = WorkItemGraph.Build([Item(1), Item(2)], [new WorkItemLink(1, 10, LinkTypes.Related)]);

        graph.GetLinks(2).ShouldNotBeNull();
        graph.GetLinks(2).ShouldBeEmpty();
    }

    [Fact]
    public void GetLinks_IdOutsideTheSet_ReturnsEmptyRatherThanThrowing()
    {
        var graph = WorkItemGraph.Build([Item(1)], [new WorkItemLink(1, 10, LinkTypes.Related)]);

        Should.NotThrow(() => graph.GetLinks(999));
        graph.GetLinks(999).ShouldBeEmpty();
    }

    /// <summary>
    /// 🔴 The documented contract: edges leaving the set are RETAINED. A consumer discovering
    /// what to fetch next needs exactly those, so filtering them away would break the model's
    /// stated purpose. This asserts the retention explicitly so a future "tidy up" that filters
    /// to intra-set edges goes red by name.
    /// </summary>
    [Fact]
    public void Links_RetainsEdgesWhoseTargetIsOutsideTheSet()
    {
        var items = new[] { Item(1), Item(2) };
        var outward = new WorkItemLink(1, 777, LinkTypes.Successor);
        var inward = new WorkItemLink(1, 2, LinkTypes.Predecessor);

        var graph = WorkItemGraph.Build(items, [outward, inward]);

        graph.ContainsItem(777).ShouldBeFalse();
        graph.Links.ShouldContain(outward);
        graph.GetLinks(1).ShouldContain(outward);
        // And the intra-set edge is there too, so this is not passing by returning everything blindly.
        graph.GetLinks(1).ShouldContain(inward);
        graph.GetLinks(1).Count.ShouldBe(2);
    }

    [Fact]
    public void ContainsItem_DistinguishesMembersFromNonMembers()
    {
        var graph = WorkItemGraph.Build([Item(1), Item(2)]);

        graph.ContainsItem(1).ShouldBeTrue();
        graph.ContainsItem(2).ShouldBeTrue();
        graph.ContainsItem(3).ShouldBeFalse();
    }

    [Fact]
    public void Build_PreservesCallerItemOrder()
    {
        var graph = WorkItemGraph.Build([Item(30), Item(10), Item(20)]);

        graph.Items.Select(i => i.Id).ShouldBe([30, 10, 20]);
    }

    [Fact]
    public void Build_WithoutLinks_YieldsAnEdgelessGraphRatherThanThrowing()
    {
        var graph = WorkItemGraph.Build([Item(1)]);

        graph.Links.ShouldBeEmpty();
        graph.GetLinks(1).ShouldBeEmpty();
        graph.Items.Count.ShouldBe(1);
    }

    [Fact]
    public void Build_EmptySet_IsLegal()
    {
        var graph = WorkItemGraph.Build([]);

        graph.Items.ShouldBeEmpty();
        graph.Links.ShouldBeEmpty();
        graph.ContainsItem(1).ShouldBeFalse();
    }

    [Fact]
    public void Build_NullItems_Throws()
    {
        Should.Throw<ArgumentNullException>(() => WorkItemGraph.Build(null!));
    }
}
