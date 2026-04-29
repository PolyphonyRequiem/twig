using Shouldly;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

/// <summary>
/// Unit tests for <see cref="SeedDependencyGraph.WouldCreateCycle"/>.
/// </summary>
public class SeedDependencyGraphCycleTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Direct cycle (A→B, B→A)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_DirectCycle_ReturnsTrue()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-2, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Transitive cycle (A→B→C→A)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_TransitiveCycle_ReturnsTrue()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-3, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Self-loop (A→A)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_SelfLoop_ReturnsTrue()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 1).Build(),
        };
        var proposed = new SeedLink(-1, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, Array.Empty<SeedLink>(), proposed).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParentId edges included in cycle detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_ParentIdEdgeFormsCycle_ReturnsTrue()
    {
        // B.ParentId = A → edge A→B. Proposing DependsOn A→B means edge B→A.
        // Combined: A→B (parent) and B→A (depends-on) = cycle.
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).WithParent(-1).Build(),
        };
        var proposed = new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, Array.Empty<SeedLink>(), proposed).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Non-directional Related link excluded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_RelatedLink_ReturnsFalse()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        // Related is non-directional and should not create a cycle
        var proposed = new SeedLink(-2, -1, SeedLinkTypes.Related, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Non-directional ParentChild link excluded
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_ParentChildLink_ReturnsFalse()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-2, -1, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  No cycle — valid graph (shortcut edge)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_ValidShortcutEdge_ReturnsFalse()
    {
        // A→B→C already. Proposing A→C is a shortcut, not a cycle.
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-1, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Proposed link endpoint not in seed set
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_EndpointNotInSeeds_ReturnsFalse()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 1).Build(),
        };
        // -99 is not in the seed set
        var proposed = new SeedLink(-1, -99, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, Array.Empty<SeedLink>(), proposed).ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mixed link types forming cycle (Blocks + DependsOn)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_MixedBlocksAndDependsOn_ReturnsTrue()
    {
        // A blocks B → edge A→B.  Proposing DependsOn A→B → edge B→A.  Cycle: A→B→A.
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Successor/Predecessor links (non-directional in graph)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WouldCreateCycle_SuccessorLink_ReturnsFalse()
    {
        // Successor is not in the AddLinkEdge switch — treated as non-directional
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-2, -1, SeedLinkTypes.Successor, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeFalse();
    }

    [Fact]
    public void WouldCreateCycle_PredecessorLink_ReturnsFalse()
    {
        var seeds = new[]
        {
            new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build(),
            new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build(),
        };
        var existingLinks = new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        var proposed = new SeedLink(-2, -1, SeedLinkTypes.Predecessor, DateTimeOffset.UtcNow);

        SeedDependencyGraph.WouldCreateCycle(seeds, existingLinks, proposed).ShouldBeFalse();
    }
}
