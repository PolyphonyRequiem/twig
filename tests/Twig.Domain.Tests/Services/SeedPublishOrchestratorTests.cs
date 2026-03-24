using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SeedPublishOrchestrator.PublishAsync"/>.
/// All dependencies are mocked via NSubstitute.
/// </summary>
public class SeedPublishOrchestratorTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly ISeedLinkRepository _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
    private readonly IPublishIdMapRepository _publishIdMapRepo = Substitute.For<IPublishIdMapRepository>();
    private readonly ISeedPublishRulesProvider _rulesProvider = Substitute.For<ISeedPublishRulesProvider>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    private readonly SeedPublishOrchestrator _orchestrator;

    public SeedPublishOrchestratorTests()
    {
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);

        _orchestrator = new SeedPublishOrchestrator(
            _workItemRepo,
            _adoService,
            _seedLinkRepo,
            _publishIdMapRepo,
            _rulesProvider,
            _unitOfWork);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Guard: already-published (positive ID) → Skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_PositiveId_ReturnsSkipped()
    {
        var result = await _orchestrator.PublishAsync(42);

        result.Status.ShouldBe(SeedPublishStatus.Skipped);
        result.OldId.ShouldBe(42);
        result.NewId.ShouldBe(42);
        result.IsSuccess.ShouldBeTrue();

        // No ADO calls should be made
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Guard: seed not found → Error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_SeedNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Error);
        result.ErrorMessage!.ShouldContain("not found");
        result.IsSuccess.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Guard: not a seed → Error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_NotASeed_ReturnsError()
    {
        var nonSeed = new WorkItemBuilder(-1, "Not a seed").Build(); // IsSeed = false
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(nonSeed);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Error);
        result.ErrorMessage!.ShouldContain("not a seed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Guard: parent is unpublished seed (negative ParentId) → Error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_NegativeParentId_ReturnsError()
    {
        var seed = new WorkItemBuilder(-2, "Child seed").AsSeed().WithParent(-1).Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAsync(-2);

        result.Status.ShouldBe(SeedPublishStatus.Error);
        result.ErrorMessage!.ShouldContain("must be published first");
        result.Title.ShouldBe("Child seed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Validation failure → ValidationFailed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_ValidationFails_ReturnsValidationFailed()
    {
        var seed = new WorkItemBuilder(-1, "").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        result.ValidationFailures.ShouldNotBeEmpty();
        result.IsSuccess.ShouldBeFalse();

        // No ADO calls
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Force bypasses validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_Force_BypassesValidation()
    {
        var seed = new WorkItemBuilder(-1, "").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        SetupSuccessfulAdoFlow(-1, 500);

        var result = await _orchestrator.PublishAsync(-1, force: true);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dry run → DryRun
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_DryRun_ReturnsWithoutApiCalls()
    {
        var seed = new WorkItemBuilder(-1, "Dry seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAsync(-1, dryRun: true);

        result.Status.ShouldBe(SeedPublishStatus.DryRun);
        result.Title.ShouldBe("Dry seed");
        result.IsSuccess.ShouldBeTrue();

        // No ADO calls
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Successful publish — full transactional flow
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_Success_CreatesAndRemapsInTransaction()
    {
        var seed = new WorkItemBuilder(-1, "My seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        SetupSuccessfulAdoFlow(-1, 500);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.OldId.ShouldBe(-1);
        result.NewId.ShouldBe(500);
        result.Title.ShouldBe("My seed");
        result.IsSuccess.ShouldBeTrue();

        // Verify transactional flow
        await _unitOfWork.Received(1).BeginAsync(Arg.Any<CancellationToken>());
        await _publishIdMapRepo.Received(1).RecordMappingAsync(-1, 500, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received(1).RemapIdAsync(-1, 500, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).RemapParentIdAsync(-1, 500, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(Arg.Is<WorkItem>(w => w.Id == 500 && w.IsSeed), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitAsync(_transaction, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().RollbackAsync(Arg.Any<ITransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_Success_FetchedItemMarkedAsSeed()
    {
        var seed = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Seed").Build(); // IsSeed = false from ADO
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);

        // Verify saved item has IsSeed = true
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.IsSeed == true && w.Id == 500),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Transaction rollback on error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_TransactionError_RollsBack()
    {
        var seed = new WorkItemBuilder(-1, "Failing seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(
            new WorkItemBuilder(500, "Fetched").Build());

        // Simulate failure during transaction
        _publishIdMapRepo.RecordMappingAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("DB error")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _orchestrator.PublishAsync(-1));

        await _unitOfWork.Received(1).RollbackAsync(_transaction, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().CommitAsync(Arg.Any<ITransaction>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed with null ParentId → OK
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_NullParentId_Succeeds()
    {
        var seed = new WorkItemBuilder(-1, "Orphan seed").AsSeed().Build(); // No parent
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        SetupSuccessfulAdoFlow(-1, 500);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Seed with positive ParentId → OK
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_PositiveParentId_Succeeds()
    {
        var seed = new WorkItemBuilder(-1, "Child seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        SetupSuccessfulAdoFlow(-1, 500);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Force + Dry run → dry run wins (no API calls)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_ForceAndDryRun_ReturnsDryRun()
    {
        var seed = new WorkItemBuilder(-1, "").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAsync(-1, force: true, dryRun: true);

        result.Status.ShouldBe(SeedPublishStatus.DryRun);
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupSuccessfulAdoFlow(int seedId, int newId)
    {
        var fetchedItem = new WorkItemBuilder(newId, "Fetched").Build();
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(newId);
        _adoService.FetchAsync(newId, Arg.Any<CancellationToken>()).Returns(fetchedItem);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Link promotion integrated into PublishAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_Success_PromotesLinks()
    {
        var seed = new WorkItemBuilder(-1, "Linked seed").AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Fetched").Build();
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem);

        // After remap, link between 500 and 200 is eligible
        var links = new[]
        {
            new SeedLink(500, 200, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(500, Arg.Any<CancellationToken>()).Returns(links);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.LinkWarnings.ShouldBeEmpty();
        await _adoService.Received(1).AddLinkAsync(500, 200, "System.LinkTypes.Dependency-Reverse", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_LinkPromotionFailure_IsWarningNotError()
    {
        var seed = new WorkItemBuilder(-1, "Linked seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Fetched").Build();
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem);

        var links = new[]
        {
            new SeedLink(500, 200, SeedLinkTypes.Related, DateTimeOffset.UtcNow),
        };
        _seedLinkRepo.GetLinksForItemAsync(500, Arg.Any<CancellationToken>()).Returns(links);
        _adoService.AddLinkAsync(500, 200, "System.LinkTypes.Related", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Link error")));

        var result = await _orchestrator.PublishAsync(-1);

        // Publish still succeeds
        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.IsSuccess.ShouldBeTrue();
        // But warnings are reported
        result.LinkWarnings.Count.ShouldBe(1);
        result.LinkWarnings[0].ShouldContain("Link error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  PublishAllAsync — empty seed set
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_NoSeeds_ReturnsEmptyResult()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var result = await _orchestrator.PublishAllAsync();

        result.Results.ShouldBeEmpty();
        result.CycleErrors.ShouldBeEmpty();
        result.HasErrors.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  PublishAllAsync — topological order with ParentId remapping
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_ParentChildOrder_PublishesParentFirst()
    {
        // Parent seed -2, child seed -1 (ParentId = -2)
        var parentSeed = new WorkItemBuilder(-2, "Parent").AsSeed(daysOld: 2).Build();
        var childSeed = new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(-2).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { parentSeed, childSeed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        // When PublishAsync re-loads -2, return it as-is
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(parentSeed);

        // After parent publishes, child is re-loaded with remapped ParentId
        var childAfterRemap = new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(200).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(childAfterRemap);

        // ADO flows
        _adoService.CreateAsync(Arg.Is<WorkItem>(w => w.Id == -2), Arg.Any<CancellationToken>()).Returns(200);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(200, "Parent").Build());
        _adoService.CreateAsync(Arg.Is<WorkItem>(w => w.Id == -1), Arg.Any<CancellationToken>()).Returns(201);
        _adoService.FetchAsync(201, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(201, "Child").Build());

        // No links to promote
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.Results.Count.ShouldBe(2);
        result.CycleErrors.ShouldBeEmpty();
        result.HasErrors.ShouldBeFalse();

        // Verify publish order: parent first, then child
        result.Results[0].OldId.ShouldBe(-2);
        result.Results[0].NewId.ShouldBe(200);
        result.Results[1].OldId.ShouldBe(-1);
        result.Results[1].NewId.ShouldBe(201);
    }

    // ═══════════════════════════════════════════════════════════════
    //  PublishAllAsync — circular dependency detected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_CyclicDeps_ReportsCycleError()
    {
        var seedA = new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build();
        var seedB = new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seedA, seedB });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        var result = await _orchestrator.PublishAllAsync();

        result.CycleErrors.Count.ShouldBe(1);
        result.CycleErrors[0].ShouldContain("Circular dependency");
        result.Results.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  PublishAllAsync — dependency chain order
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_DependencyChain_CorrectOrder()
    {
        // -2 depends-on -3, -1 depends-on -2
        var seed3 = new WorkItemBuilder(-3, "Root").AsSeed(daysOld: 1).Build();
        var seed2 = new WorkItemBuilder(-2, "Mid").AsSeed(daysOld: 2).Build();
        var seed1 = new WorkItemBuilder(-1, "Leaf").AsSeed(daysOld: 3).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed1, seed2, seed3 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-2, -3, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        // Re-load returns the same seeds
        _workItemRepo.GetByIdAsync(-3, Arg.Any<CancellationToken>()).Returns(seed3);
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed2);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed1);

        // ADO flows with unique IDs
        _adoService.CreateAsync(Arg.Is<WorkItem>(w => w.Id == -3), Arg.Any<CancellationToken>()).Returns(300);
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(300, "Root").Build());
        _adoService.CreateAsync(Arg.Is<WorkItem>(w => w.Id == -2), Arg.Any<CancellationToken>()).Returns(301);
        _adoService.FetchAsync(301, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(301, "Mid").Build());
        _adoService.CreateAsync(Arg.Is<WorkItem>(w => w.Id == -1), Arg.Any<CancellationToken>()).Returns(302);
        _adoService.FetchAsync(302, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(302, "Leaf").Build());

        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.Results.Count.ShouldBe(3);
        result.Results[0].OldId.ShouldBe(-3);
        result.Results[1].OldId.ShouldBe(-2);
        result.Results[2].OldId.ShouldBe(-1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  PublishAllAsync — dry run propagates to all seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_DryRun_AllResultsDryRun()
    {
        var seed = new WorkItemBuilder(-1, "Dry seed").AsSeed(daysOld: 1).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAllAsync(dryRun: true);

        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.DryRun);
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }
}
