using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Tests for <see cref="WorkingSet"/> value object AllIds computation.
/// Infrastructure-level eviction tests live in Twig.Infrastructure.Tests.
/// </summary>
public class WorkingSetAllIdsTests
{
    private static readonly IterationPath TestIteration = IterationPath.Parse(@"Project\Sprint1").Value;

    // ═══════════════════════════════════════════════════════════════
    //  AllIds contains active item when set
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_WithActiveItem_ContainsActiveId()
    {
        var ws = new WorkingSet
        {
            ActiveItemId = 42,
            IterationPath = TestIteration,
        };

        ws.AllIds.ShouldContain(42);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds empty when no items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_NoItems_IsEmpty()
    {
        var ws = new WorkingSet
        {
            IterationPath = TestIteration,
        };

        ws.AllIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds union of all categories
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_UnionOfAllCategories()
    {
        var ws = new WorkingSet
        {
            ActiveItemId = 1,
            ParentChainIds = [100, 1],
            ChildrenIds = [10, 11],
            SprintItemIds = [50, 51],
            SeedIds = [-1, -2],
            DirtyItemIds = new HashSet<int> { 99 },
            IterationPath = TestIteration,
        };

        ws.AllIds.ShouldBe(new HashSet<int> { 1, 100, 10, 11, 50, 51, -1, -2, 99 }, ignoreOrder: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds deduplicates overlapping IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_OverlappingIds_NoDuplicates()
    {
        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ParentChainIds = [10, 20],
            ChildrenIds = [20, 30],
            SprintItemIds = [30, 40],
            SeedIds = [],
            DirtyItemIds = new HashSet<int> { 10, 40 },
            IterationPath = TestIteration,
        };

        ws.AllIds.Count.ShouldBe(4); // {10, 20, 30, 40}
        ws.AllIds.ShouldBe(new HashSet<int> { 10, 20, 30, 40 }, ignoreOrder: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds without active item — null excluded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_NullActiveItem_NotIncluded()
    {
        var ws = new WorkingSet
        {
            ActiveItemId = null,
            ParentChainIds = [5],
            IterationPath = TestIteration,
        };

        ws.AllIds.ShouldBe(new HashSet<int> { 5 }, ignoreOrder: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  AllIds reflects updated properties after with-expression
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllIds_AfterWithExpression_ReflectsUpdatedProperties()
    {
        var original = new WorkingSet
        {
            ActiveItemId = 1,
            ParentChainIds = [100],
            IterationPath = TestIteration,
        };

        var clone = original with { ActiveItemId = 2, ParentChainIds = [200] };

        clone.AllIds.ShouldContain(2);
        clone.AllIds.ShouldContain(200);
        clone.AllIds.ShouldNotContain(1);
        clone.AllIds.ShouldNotContain(100);

        // Original unchanged
        original.AllIds.ShouldContain(1);
        original.AllIds.ShouldContain(100);
        original.AllIds.ShouldNotContain(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  IterationPath preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void IterationPath_Preserved()
    {
        var ws = new WorkingSet
        {
            IterationPath = TestIteration,
        };

        ws.IterationPath.ShouldBe(TestIteration);
    }
}
