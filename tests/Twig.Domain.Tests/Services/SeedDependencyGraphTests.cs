using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SeedDependencyGraph"/>.
/// </summary>
public class SeedDependencyGraphTests
{
    // ═══════════════════════════════════════════════════════════════
    //  No dependencies → creation order (SeedCreatedAt)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_NoDependencies_ReturnsCreationOrder()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-3, "Third").AsSeed(daysOld: 1).Build(),
            new WorkItemBuilder(-1, "First").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "Second").AsSeed(daysOld: 2).Build(),
        };

        var (order, cyclicIds) = SeedDependencyGraph.Sort(seeds, Array.Empty<SeedLink>());

        // Oldest first: -1 (3 days old), -2 (2 days old), -3 (1 day old)
        order.Count.ShouldBe(3);
        order[0].ShouldBe(-1);
        order[1].ShouldBe(-2);
        order[2].ShouldBe(-3);
        cyclicIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  depends-on edge: source after target
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_DependsOn_TargetPublishedFirst()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Depends").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "Dependency").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBe(new[] { -2, -1 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  blocked-by edge: source after target
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_BlockedBy_TargetPublishedFirst()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Blocked").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "Blocker").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.BlockedBy, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBe(new[] { -2, -1 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  blocks edge: source published first (edge from target to source)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_Blocks_SourcePublishedFirst()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Blocker").AsSeed(daysOld: 1).Build(),
            new WorkItemBuilder(-2, "Blocked").AsSeed(daysOld: 2).Build(),
        };
        var links = new[]
        {
            // -1 blocks -2 → publish -1 first (edge from -2 to -1)
            new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBe(new[] { -1, -2 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  depended-on-by edge: source published first (edge from target to source)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_DependedOnBy_SourcePublishedFirst()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Depended").AsSeed(daysOld: 1).Build(),
            new WorkItemBuilder(-2, "Dependent").AsSeed(daysOld: 2).Build(),
        };
        var links = new[]
        {
            // -1 is depended-on-by -2 → publish -1 first (edge from -2 to -1)
            new SeedLink(-1, -2, SeedLinkTypes.DependedOnBy, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBe(new[] { -1, -2 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParentId < 0 edges: child waits for parent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ParentIdNegative_ParentPublishedFirst()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 2).WithParent(-2).Build(),
            new WorkItemBuilder(-2, "Parent").AsSeed(daysOld: 1).Build(),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, Array.Empty<SeedLink>());

        order.ShouldBe(new[] { -2, -1 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParentId positive → no edge (parent already published)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ParentIdPositive_NoEdge()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(100).Build(),
            new WorkItemBuilder(-2, "Other").AsSeed(daysOld: 2).Build(),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, Array.Empty<SeedLink>());

        // -2 is older, so it comes first (no dependency edge from -1 to -2)
        order.Count.ShouldBe(2);
        order[0].ShouldBe(-2);
        order[1].ShouldBe(-1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  parent-child and related link types → no ordering implication
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ParentChildLinkType_NoEdge()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        // No ordering constraint from parent-child link type; -1 is older
        order[0].ShouldBe(-1);
        order[1].ShouldBe(-2);
    }

    [Fact]
    public void TopologicalSort_RelatedLinkType_NoEdge()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        order[0].ShouldBe(-1);
        order[1].ShouldBe(-2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cycle detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_Cycle_DetectedAndReported()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        // No seeds in the result (both are cyclic)
        order.ShouldBeEmpty();
        cyclicIds.ShouldContain(-1);
        cyclicIds.ShouldContain(-2);
    }

    [Fact]
    public void TopologicalSort_CycleWithNonCyclic_PublishesNonCyclic()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            // -2 and -3 form a cycle
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-3, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        // Only -1 is publishable
        order.ShouldBe(new[] { -1 });
        cyclicIds.ShouldContain(-2);
        cyclicIds.ShouldContain(-3);
        cyclicIds.ShouldNotContain(-1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Complex chain: ParentId + dependency edges
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ComplexChain_CorrectOrder()
    {
        // Seeds: -1 child of -2, -2 depends-on -3, -3 has no deps
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(-2).Build(),
            new WorkItemBuilder(-2, "Parent").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "Root").AsSeed(daysOld: 3).Build(),
        };
        var links = new[]
        {
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, links);

        // -3 first (no deps), -2 next (depends on -3), -1 last (child of -2)
        order.ShouldBe(new[] { -3, -2, -1 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Empty input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_EmptyInput_ReturnsEmpty()
    {
        var (order, cyclicIds) = SeedDependencyGraph.Sort(Array.Empty<WorkItem>(), Array.Empty<SeedLink>());

        order.ShouldBeEmpty();
        cyclicIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Links referencing non-seed endpoints are ignored
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_LinkToNonSeed_Ignored()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Seed").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            // -1 depends-on 999 (not a seed) — should not crash or create edge
            new SeedLink(-1, 999, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBe(new[] { -1 });
        cyclicIds.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParentId < 0 but parent is not in the seed set → no edge
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ParentNotInSeedSet_NoEdge()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "Orphan child").AsSeed(daysOld: 1).WithParent(-99).Build(),
        };

        var (order, _) = SeedDependencyGraph.Sort(seeds, Array.Empty<SeedLink>());

        order.ShouldBe(new[] { -1 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Three-node cycle
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void TopologicalSort_ThreeNodeCycle_AllDetected()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build(),
        };
        var links = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -3, SeedLinkTypes.BlockedBy, DateTimeOffset.UtcNow),
            new SeedLink(-3, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };

        var (order, cyclicIds) = SeedDependencyGraph.Sort(seeds, links);

        order.ShouldBeEmpty();
        cyclicIds.Count.ShouldBe(3);
    }
}
