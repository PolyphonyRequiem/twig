using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.ReadModels;

public class WorkTreeTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Build + basic properties
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_SetsAllProperties()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus item");
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var child1 = WorkItemBuilder.Simple(20, "Child 1");
        var child2 = WorkItemBuilder.Simple(21, "Child 2");

        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child1, child2 });

        tree.FocusedItem.ShouldBe(focus);
        tree.ParentChain.Count.ShouldBe(1);
        tree.ParentChain[0].ShouldBe(parent);
        tree.Children.Count.ShouldBe(2);
    }

    [Fact]
    public void Build_ThreeLevelTree()
    {
        var grandparent = WorkItemBuilder.Simple(1, "Epic");
        var parent = WorkItemBuilder.Simple(5, "Feature");
        var focus = WorkItemBuilder.Simple(10, "User Story");
        var child1 = WorkItemBuilder.Simple(20, "Task A");
        var child2 = WorkItemBuilder.Simple(21, "Task B");
        var child3 = WorkItemBuilder.Simple(22, "Task C");

        var tree = WorkTree.Build(focus, new[] { grandparent, parent }, new[] { child1, child2, child3 });

        tree.FocusedItem.Id.ShouldBe(10);
        tree.ParentChain.Count.ShouldBe(2);
        tree.ParentChain[0].Id.ShouldBe(1);
        tree.ParentChain[1].Id.ShouldBe(5);
        tree.Children.Count.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MoveUp
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MoveUp_FromChild_ReturnsParentId()
    {
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var focus = WorkItemBuilder.Simple(10, "Focus");

        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>());

        tree.MoveUp().ShouldBe(5);
    }

    [Fact]
    public void MoveUp_FromRoot_ReturnsNull()
    {
        var focus = WorkItemBuilder.Simple(10, "Root");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        tree.MoveUp().ShouldBeNull();
    }

    [Fact]
    public void MoveUp_ThreeLevels_ReturnsImmediateParent()
    {
        var grandparent = WorkItemBuilder.Simple(1, "Grandparent");
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var focus = WorkItemBuilder.Simple(10, "Focus");

        var tree = WorkTree.Build(focus, new[] { grandparent, parent }, Array.Empty<WorkItem>());

        // MoveUp returns the last item in ParentChain (immediate parent)
        tree.MoveUp().ShouldBe(5);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MoveDown — exact ID
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MoveDown_ExactId_ReturnsChildId()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child = WorkItemBuilder.Simple(20, "Child");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = tree.MoveDown("20");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(20);
    }

    [Fact]
    public void MoveDown_ExactId_NotFound_Fails()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child = WorkItemBuilder.Simple(20, "Child");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = tree.MoveDown("999");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No child matches");
    }

    // ═══════════════════════════════════════════════════════════════
    //  MoveDown — pattern single match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MoveDown_PatternSingleMatch_ReturnsChildId()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child1 = WorkItemBuilder.Simple(20, "Fix login bug");
        var child2 = WorkItemBuilder.Simple(21, "Add dashboard");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = tree.MoveDown("dashboard");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(21);
    }

    // ═══════════════════════════════════════════════════════════════
    //  MoveDown — pattern multi match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MoveDown_PatternMultiMatch_ReturnsAmbiguousError()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child1 = WorkItemBuilder.Simple(20, "Fix bug A");
        var child2 = WorkItemBuilder.Simple(21, "Fix bug B");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = tree.MoveDown("bug");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("Ambiguous");
    }

    // ═══════════════════════════════════════════════════════════════
    //  MoveDown — pattern no match
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MoveDown_PatternNoMatch_ReturnsError()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child = WorkItemBuilder.Simple(20, "Alpha task");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = tree.MoveDown("nonexistent");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldContain("No child matches");
    }

    // ═══════════════════════════════════════════════════════════════
    //  FindByPattern
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void FindByPattern_DelegatesToPatternMatcher()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child1 = WorkItemBuilder.Simple(20, "Login flow");
        var child2 = WorkItemBuilder.Simple(21, "Logout flow");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 });

        var result = tree.FindByPattern("Login");

        result.ShouldBeOfType<MatchResult.SingleMatch>()
              .Id.ShouldBe(20);
    }

    [Fact]
    public void FindByPattern_NumericId_MatchesById()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var child = WorkItemBuilder.Simple(20, "Some task");

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child });

        var result = tree.FindByPattern("20");

        result.ShouldBeOfType<MatchResult.SingleMatch>()
              .Id.ShouldBe(20);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyChildren_MoveDown_NoMatch()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = tree.MoveDown("anything");

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void EmptyChildren_FindByPattern_NoMatch()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        var result = tree.FindByPattern("anything");

        result.ShouldBeOfType<MatchResult.NoMatch>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  SiblingCounts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_WithoutSiblingCounts_DefaultsToNull()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        tree.SiblingCounts.ShouldBeNull();
    }

    [Fact]
    public void Build_WithSiblingCounts_StoresValues()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var counts = new Dictionary<int, int?> { [5] = null, [10] = 3 };

        var tree = WorkTree.Build(focus, new[] { parent }, Array.Empty<WorkItem>(), counts);

        tree.SiblingCounts.ShouldNotBeNull();
        tree.SiblingCounts[5].ShouldBeNull();
        tree.SiblingCounts[10].ShouldBe(3);
    }

    [Fact]
    public void Build_BackwardCompat_ExistingCallersCompileWithoutChanges()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var child = WorkItemBuilder.Simple(20, "Child");

        // Three-parameter overload — must still work
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child });

        tree.FocusedItem.Id.ShouldBe(10);
        tree.ParentChain.Count.ShouldBe(1);
        tree.Children.Count.ShouldBe(1);
        tree.SiblingCounts.ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  DescendantsByParentId
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Build_WithoutDescendants_DefaultsToEmptyDictionary()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        tree.DescendantsByParentId.ShouldNotBeNull();
        tree.DescendantsByParentId.Count.ShouldBe(0);
    }

    [Fact]
    public void Build_WithDescendants_StoresValues()
    {
        var focus = WorkItemBuilder.Simple(10, "Epic");
        var child = WorkItemBuilder.Simple(20, "Feature");
        var grandchild1 = WorkItemBuilder.Simple(30, "Story A");
        var grandchild2 = WorkItemBuilder.Simple(31, "Story B");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { child },
            [20] = new[] { grandchild1, grandchild2 }
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        tree.DescendantsByParentId.Count.ShouldBe(2);
        tree.DescendantsByParentId[10].Count.ShouldBe(1);
        tree.DescendantsByParentId[20].Count.ShouldBe(2);
    }

    [Fact]
    public void GetDescendants_KnownParent_ReturnsChildren()
    {
        var focus = WorkItemBuilder.Simple(10, "Epic");
        var child = WorkItemBuilder.Simple(20, "Feature");
        var grandchild = WorkItemBuilder.Simple(30, "Story");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { child },
            [20] = new[] { grandchild }
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        tree.GetDescendants(20).Count.ShouldBe(1);
        tree.GetDescendants(20)[0].Id.ShouldBe(30);
    }

    [Fact]
    public void GetDescendants_UnknownParent_ReturnsEmptyList()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        tree.GetDescendants(999).ShouldBeEmpty();
    }

    [Fact]
    public void GetDescendants_LeafNode_ReturnsEmptyList()
    {
        var focus = WorkItemBuilder.Simple(10, "Epic");
        var child = WorkItemBuilder.Simple(20, "Task");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [10] = new[] { child }
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child },
            descendantsByParentId: descendants);

        // Task 20 has no children in the descendants map
        tree.GetDescendants(20).ShouldBeEmpty();
    }

    [Fact]
    public void Build_BackwardCompat_DescendantsDefaultsWhenOmitted()
    {
        var focus = WorkItemBuilder.Simple(10, "Focus");
        var parent = WorkItemBuilder.Simple(5, "Parent");
        var child = WorkItemBuilder.Simple(20, "Child");
        var counts = new Dictionary<int, int?> { [10] = 3 };

        // Five-parameter call (before descendantsByParentId existed) — still works
        var tree = WorkTree.Build(focus, new[] { parent }, new[] { child }, counts,
            Array.Empty<WorkItemLink>());

        tree.DescendantsByParentId.ShouldNotBeNull();
        tree.DescendantsByParentId.Count.ShouldBe(0);
    }

    [Fact]
    public void Build_WithDescendants_DeepHierarchy_ThreeLevels()
    {
        var focus = WorkItemBuilder.Simple(1, "Epic");
        var child1 = WorkItemBuilder.Simple(10, "Feature A");
        var child2 = WorkItemBuilder.Simple(11, "Feature B");
        var grandchild1 = WorkItemBuilder.Simple(100, "Story A1");
        var grandchild2 = WorkItemBuilder.Simple(101, "Story A2");
        var greatGrandchild = WorkItemBuilder.Simple(1000, "Task A1a");

        var descendants = new Dictionary<int, IReadOnlyList<WorkItem>>
        {
            [1] = new[] { child1, child2 },
            [10] = new[] { grandchild1, grandchild2 },
            [100] = new[] { greatGrandchild }
        };

        var tree = WorkTree.Build(focus, Array.Empty<WorkItem>(), new[] { child1, child2 },
            descendantsByParentId: descendants);

        tree.DescendantsByParentId.Count.ShouldBe(3);
        tree.GetDescendants(1).Count.ShouldBe(2);
        tree.GetDescendants(10).Count.ShouldBe(2);
        tree.GetDescendants(100).Count.ShouldBe(1);
        tree.GetDescendants(100)[0].Id.ShouldBe(1000);
        tree.GetDescendants(11).ShouldBeEmpty(); // Feature B has no children
    }

}
