using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SeedReconcileOrchestrator.ReconcileAsync"/>.
/// All dependencies are mocked via NSubstitute.
/// </summary>
public class SeedReconcileOrchestratorTests
{
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPublishIdMapRepository _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();

    private readonly SeedReconcileOrchestrator _orchestrator;

    public SeedReconcileOrchestratorTests()
    {
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>())
            .Returns(new List<SeedLink>());
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem>());

        _orchestrator = new SeedReconcileOrchestrator(
            _seedLinkRepo, _workItemRepo, _publishIdMapRepo);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Nothing to reconcile (clean state)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_NoLinksNoSeeds_NothingToDo()
    {
        var result = await _orchestrator.ReconcileAsync();

        result.NothingToDo.ShouldBeTrue();
        result.LinksRepaired.ShouldBe(0);
        result.LinksRemoved.ShouldBe(0);
        result.ParentIdsFixed.ShouldBe(0);
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_AllLinksHealthy_NothingToDo()
    {
        // Both endpoints exist — nothing stale
        var links = new List<SeedLink>
        {
            new(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _orchestrator.ReconcileAsync();

        result.NothingToDo.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stale link reference → remap via publish_id_map
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_StaleSourceId_RemapsViaPublishMap()
    {
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRepaired.ShouldBe(1);
        result.LinksRemoved.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemapIdAsync(-1, 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_StaleTargetId_RemapsViaPublishMap()
    {
        var links = new List<SeedLink>
        {
            new(100, -3, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-3, 600) });
        _workItemRepo.ExistsByIdAsync(100, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRepaired.ShouldBe(1);
        await _seedLinkRepo.Received(1).RemapIdAsync(-3, 600, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_BothEndpointsStaleWithMapping_RemapsBoth()
    {
        var links = new List<SeedLink>
        {
            new(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500), (-2, 600) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRepaired.ShouldBe(2);
        result.LinksRemoved.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemapIdAsync(-1, 500, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received(1).RemapIdAsync(-2, 600, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Orphaned link (no mapping) → delete
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_OrphanedSourceNoMapping_RemovesLink()
    {
        var links = new List<SeedLink>
        {
            new(-10, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRemoved.ShouldBe(1);
        result.LinksRepaired.ShouldBe(0);
        await _seedLinkRepo.Received(1).RemoveLinkAsync(-10, 200, SeedLinkTypes.Related, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_OrphanedTargetNoMapping_RemovesLink()
    {
        var links = new List<SeedLink>
        {
            new(100, -5, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _workItemRepo.ExistsByIdAsync(100, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(-5, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRemoved.ShouldBe(1);
        await _seedLinkRepo.Received(1).RemoveLinkAsync(100, -5, SeedLinkTypes.Blocks, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_MixedStaleAndOrphaned_HandlesCorrectly()
    {
        // Link 1: source stale with mapping → remap
        // Link 2: target stale no mapping → remove
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            new(300, -99, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(300, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(-99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRepaired.ShouldBe(1);
        result.LinksRemoved.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Stale parent_id → fix via publish_id_map
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_StaleParentIdWithMapping_FixesParent()
    {
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-10, 700) });

        var seed = new WorkItemBuilder(-5, "Child seed").AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.ParentIdsFixed.ShouldBe(1);
        result.Warnings.ShouldBeEmpty();
        await _workItemRepo.Received(1).RemapParentIdAsync(-10, 700, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_StaleParentIdNoMapping_WarnsAboutDiscard()
    {
        var seed = new WorkItemBuilder(-5, "Orphan child").AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.ParentIdsFixed.ShouldBe(0);
        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("#-5");
        result.Warnings[0].ShouldContain("#-10");
        result.Warnings[0].ShouldContain("discarded");
    }

    [Fact]
    public async Task ReconcileAsync_ParentIdStillExists_NoFix()
    {
        var seed = new WorkItemBuilder(-5, "Child seed").AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _orchestrator.ReconcileAsync();

        result.ParentIdsFixed.ShouldBe(0);
        result.Warnings.ShouldBeEmpty();
        await _workItemRepo.DidNotReceive().RemapParentIdAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_PositiveParentId_Ignored()
    {
        var seed = new WorkItemBuilder(-5, "Published parent").AsSeed().WithParent(100).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });

        var result = await _orchestrator.ReconcileAsync();

        result.NothingToDo.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Deduplication: same stale ID in multiple links → single remap
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_SameStaleIdInMultipleLinks_RemapsOnce()
    {
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            new(-1, 300, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(300, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _orchestrator.ReconcileAsync();

        // RemapIdAsync remaps ALL references to -1 in one call, so only counted once
        result.LinksRepaired.ShouldBe(1);
        await _seedLinkRepo.Received(1).RemapIdAsync(-1, 500, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Combined: links + parent fixes in one pass
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReconcileAsync_LinksAndParents_CombinedResult()
    {
        // One link to remap, one link to remove, one parent to fix
        var links = new List<SeedLink>
        {
            new(-1, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
            new(300, -99, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(links);
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500), (-10, 700) });
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(200, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(300, Arg.Any<CancellationToken>()).Returns(true);
        _workItemRepo.ExistsByIdAsync(-99, Arg.Any<CancellationToken>()).Returns(false);

        var seed = new WorkItemBuilder(-5, "Child").AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Aggregates.WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _orchestrator.ReconcileAsync();

        result.LinksRepaired.ShouldBe(1);
        result.LinksRemoved.ShouldBe(1);
        result.ParentIdsFixed.ShouldBe(1);
        result.NothingToDo.ShouldBeFalse();
    }
}
