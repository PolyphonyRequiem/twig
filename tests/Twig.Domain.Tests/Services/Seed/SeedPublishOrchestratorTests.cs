using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Seed;

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
    private readonly IFieldDefinitionStore _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();

    private readonly SeedPublishOrchestrator _orchestrator;

    public SeedPublishOrchestratorTests()
    {
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
        _rulesProvider.GetRulesAsync(Arg.Any<CancellationToken>()).Returns(SeedPublishRules.Default);

        var backlogOrderer = new BacklogOrderer(_adoService, _fieldDefinitionStore);
        _orchestrator = new SeedPublishOrchestrator(
            _workItemRepo,
            _adoService,
            _seedLinkRepo,
            _publishIdMapRepo,
            _rulesProvider,
            _unitOfWork,
            backlogOrderer);
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
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
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
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
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
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
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
        await _workItemRepo.Received(2).SaveAsync(Arg.Is<WorkItem>(w => w.Id == 500 && w.IsSeed), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitAsync(_transaction, Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().RollbackAsync(Arg.Any<ITransaction>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_Success_FetchedItemMarkedAsSeed()
    {
        var seed = new WorkItemBuilder(-1, "Seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Seed").Build(); // IsSeed = false from ADO
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);

        // Verify saved items have IsSeed = true (transactional save + post-publish refresh)
        await _workItemRepo.Received(2).SaveAsync(
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
        SetupSuccessfulAdoFlow(-1, 500);

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
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupSuccessfulAdoFlow(int seedId, int newId)
    {
        var fetchedItem = new WorkItemBuilder(newId, "Fetched").Build();
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(newId);
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
        SetupSuccessfulAdoFlow(-1, 500);

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
        SetupSuccessfulAdoFlow(-1, 500);

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
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Parent"), Arg.Any<CancellationToken>()).Returns(200);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(200, "Parent").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Child"), Arg.Any<CancellationToken>()).Returns(201);
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
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Root"), Arg.Any<CancellationToken>()).Returns(300);
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(300, "Root").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Mid"), Arg.Any<CancellationToken>()).Returns(301);
        _adoService.FetchAsync(301, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(301, "Mid").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Leaf"), Arg.Any<CancellationToken>()).Returns(302);
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
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Post-publish cache refresh
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAsync_Success_RefreshesCacheAfterPostPublishSteps()
    {
        var seed = new WorkItemBuilder(-1, "My seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        // First fetch (step 8) returns Rev 1; second fetch (step 12b refresh) returns Rev 3
        var fetchedItem = new WorkItemBuilder(500, "Fetched").Build();
        fetchedItem.MarkSynced(1);
        var refreshedItem = new WorkItemBuilder(500, "Refreshed").Build();
        refreshedItem.MarkSynced(3);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem, refreshedItem);

        var result = await _orchestrator.PublishAsync(-1);

        result.Status.ShouldBe(SeedPublishStatus.Created);

        // FetchAsync called twice: once at step 8 (initial), once at step 12b (refresh)
        await _adoService.Received(2).FetchAsync(500, Arg.Any<CancellationToken>());

        // SaveAsync called twice: once at step 10e (transaction), once at step 12b (refresh)
        await _workItemRepo.Received(2).SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());

        // Second SaveAsync receives the higher-revision item from post-publish refresh with IsSeed preserved
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 500 && w.Revision == 3 && w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_RefreshFailure_StillReturnsSuccess()
    {
        var seed = new WorkItemBuilder(-1, "My seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Fetched").Build();
        fetchedItem.MarkSynced(1);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);

        // First FetchAsync succeeds (step 8), second throws (step 12b post-publish refresh)
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>())
            .Returns(
                _ => fetchedItem,
                _ => throw new InvalidOperationException("ADO unavailable"));

        var result = await _orchestrator.PublishAsync(-1);

        // Publish still returns Created — refresh failure is non-fatal
        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
        result.IsSuccess.ShouldBeTrue();

        // SaveAsync called only once (transactional save); refresh save never reached
        await _workItemRepo.Received(1).SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_CacheRefreshSaveFails_StillReturnsSuccess()
    {
        var seed = new WorkItemBuilder(-1, "My seed").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var fetchedItem = new WorkItemBuilder(500, "Fetched").Build();
        var refreshedItem = new WorkItemBuilder(500, "Refreshed").Build();
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(fetchedItem, refreshedItem);

        // First SaveAsync succeeds (step 10e in transaction), second throws (step 12b refresh)
        _workItemRepo.SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask, Task.FromException(new InvalidOperationException("DB write error")));

        var result = await _orchestrator.PublishAsync(-1);

        // Publish still succeeds despite save failure
        result.Status.ShouldBe(SeedPublishStatus.Created);
        result.NewId.ShouldBe(500);
        result.IsSuccess.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: cycles abort entire batch — no ADO calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_CyclicDeps_NoAdoCalls()
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

        result.HasErrors.ShouldBeTrue();
        result.PreFlightErrors.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_CyclicDeps_ForceTrue_StillAborts()
    {
        var seedA = new WorkItemBuilder(-1, "A").AsSeed(daysOld: 2).Build();
        var seedB = new WorkItemBuilder(-2, "B").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seedA, seedB });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        var result = await _orchestrator.PublishAllAsync(force: true);

        result.CycleErrors.Count.ShouldBe(1);
        result.Results.ShouldBeEmpty();
        result.HasErrors.ShouldBeTrue();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_PartialCycle_AbortsBatchIncludingNonCyclicSeeds()
    {
        // Seed -3 is not in the cycle, but should still be blocked
        var seedA = new WorkItemBuilder(-1, "A").AsSeed(daysOld: 3).Build();
        var seedB = new WorkItemBuilder(-2, "B").AsSeed(daysOld: 2).Build();
        var seedC = new WorkItemBuilder(-3, "C").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seedA, seedB, seedC });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new SeedLink(-1, -2, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
            new SeedLink(-2, -1, SeedLinkTypes.DependsOn, DateTimeOffset.UtcNow),
        });

        var result = await _orchestrator.PublishAllAsync();

        result.CycleErrors.Count.ShouldBe(1);
        result.Results.ShouldBeEmpty();
        result.HasErrors.ShouldBeTrue();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: validation failures abort batch — no ADO calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_ValidationFailure_AbortsBatch_NoAdoCalls()
    {
        // Seed with empty title fails default validation (System.Title required)
        var seed = new WorkItemBuilder(-1, "").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        result.Results[0].OldId.ShouldBe(-1);
        result.CycleErrors.ShouldBeEmpty();
        result.PreFlightErrors.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_ValidationFailure_ForceTrue_BypassesValidation()
    {
        // Seed with empty title would fail validation, but force=true bypasses it
        var seed = new WorkItemBuilder(-1, "").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Published").Build());

        var result = await _orchestrator.PublishAllAsync(force: true);

        result.HasErrors.ShouldBeFalse();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.Created);
        await _adoService.Received(1).CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_MixedValidAndInvalid_AbortsBatch()
    {
        // One valid seed and one invalid seed — both should be blocked
        var validSeed = new WorkItemBuilder(-1, "Valid seed").AsSeed(daysOld: 2).Build();
        var invalidSeed = new WorkItemBuilder(-2, "").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { validSeed, invalidSeed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        // Only the invalid seed gets a ValidationFailed result
        result.Results.Count.ShouldBe(1);
        result.Results[0].OldId.ShouldBe(-2);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: orphaned parent reference — no ADO calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_OrphanedParentRef_AbortsBatch()
    {
        // Seed -1 references parent seed -99 which is not in the batch
        var seed = new WorkItemBuilder(-1, "Orphan child").AsSeed(daysOld: 1).WithParent(-99).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.PreFlightErrors.Count.ShouldBe(1);
        result.PreFlightErrors[0].ShouldContain("-99");
        result.PreFlightErrors[0].ShouldContain("not in the current batch");
        result.CycleErrors.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_OrphanedParentRef_ForceTrue_BypassesCheck()
    {
        // force=true bypasses parent reference validation
        var seed = new WorkItemBuilder(-1, "Orphan child").AsSeed(daysOld: 1).WithParent(-99).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Published").Build());

        var result = await _orchestrator.PublishAllAsync(force: true);

        // force=true bypasses pre-flight checks 2 & 3
        result.PreFlightErrors.ShouldBeEmpty();
    }

    [Fact]
    public async Task PublishAllAsync_PositiveParentId_NotFlagged()
    {
        // Positive ParentId references a published ADO item — no validation needed
        var seed = new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(100).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Published").Build());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.Created);
    }

    [Fact]
    public async Task PublishAllAsync_NegativeParentInBatch_NotFlagged()
    {
        // Seed -1 references parent seed -2 which IS in the batch — no error
        var parentSeed = new WorkItemBuilder(-2, "Parent").AsSeed(daysOld: 2).Build();
        var childSeed = new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(-2).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { parentSeed, childSeed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(parentSeed);
        var childAfterRemap = new WorkItemBuilder(-1, "Child").AsSeed(daysOld: 1).WithParent(200).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(childAfterRemap);

        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Parent"), Arg.Any<CancellationToken>()).Returns(200);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(200, "Parent").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Child"), Arg.Any<CancellationToken>()).Returns(201);
        _adoService.FetchAsync(201, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(201, "Child").Build());
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
        result.Results.Count.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: combined validation + orphaned parent errors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_ValidationAndOrphanedParent_BothReported()
    {
        // Seed -1: invalid (empty title) + orphaned parent -99
        var seed = new WorkItemBuilder(-1, "").AsSeed(daysOld: 1).WithParent(-99).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.ValidationFailed);
        result.PreFlightErrors.Count.ShouldBe(1);
        result.PreFlightErrors[0].ShouldContain("-99");
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: negative ID escape guard (I-2) — no ADO calls
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_SeedIdEscape_AbortsBatch()
    {
        // Seed -1 has a field containing "-2" which is another seed's ID → escape violation
        var seed1 = new WorkItemBuilder(-1, "Seed A").AsSeed(daysOld: 2)
            .WithField("System.Description", "Depends on -2")
            .Build();
        var seed2 = new WorkItemBuilder(-2, "Seed B").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed1, seed2 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.PreFlightErrors.Count.ShouldBe(1);
        result.PreFlightErrors[0].ShouldContain("-2");
        result.PreFlightErrors[0].ShouldContain("seed ID");
        result.CycleErrors.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_SeedIdEscape_ForceTrue_BypassesCheck()
    {
        // force=true bypasses I-2 check
        var seed1 = new WorkItemBuilder(-1, "Seed A").AsSeed(daysOld: 2)
            .WithField("System.Description", "Depends on -2")
            .Build();
        var seed2 = new WorkItemBuilder(-2, "Seed B").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed1, seed2 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed1);
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed2);
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Seed B"), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Seed B").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Seed A"), Arg.Any<CancellationToken>()).Returns(501);
        _adoService.FetchAsync(501, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(501, "Seed A").Build());

        var result = await _orchestrator.PublishAllAsync(force: true);

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
    }

    [Fact]
    public async Task PublishAllAsync_SeedIdEscape_MultipleFieldsMultipleSeeds_AllReported()
    {
        // Seed -1 has two fields with escape violations, seed -2 has one
        var seed1 = new WorkItemBuilder(-1, "Seed A").AsSeed(daysOld: 2)
            .WithField("System.Description", "See -2 and -3")
            .Build();
        var seed2 = new WorkItemBuilder(-2, "Seed B").AsSeed(daysOld: 1)
            .WithField("Custom.Notes", "Ref to -1")
            .Build();
        var seed3 = new WorkItemBuilder(-3, "Seed C").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed1, seed2, seed3 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        // seed1 has 2 escape failures (System.Description contains -2 and -3),
        // seed2 has 1 (Custom.Notes contains -1)
        result.PreFlightErrors.Count.ShouldBe(3);
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAllAsync_NoSeedIdEscape_FieldWithNonSeedNegativeNumber_Passes()
    {
        // Field contains "-999" which is not a seed ID — should pass
        var seed = new WorkItemBuilder(-1, "Seed A").AsSeed(daysOld: 1)
            .WithField("System.Description", "Temperature was -999 degrees")
            .Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Seed A").Build());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: valid graph — all checks pass, publish proceeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_ValidGraph_AllPreFlightChecksPassed_AllCreated()
    {
        var parent = new WorkItemBuilder(-1, "Parent").AsSeed(daysOld: 2).Build();
        var child = new WorkItemBuilder(-2, "Child").AsSeed(daysOld: 1).WithParent(-1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { parent, child });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        var childAfterRemap = new WorkItemBuilder(-2, "Child").AsSeed(daysOld: 1).WithParent(500).Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(childAfterRemap);

        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Parent"), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(500, "Parent").Build());
        _adoService.CreateAsync(Arg.Is<CreateWorkItemRequest>(r => r.Title == "Child"), Arg.Any<CancellationToken>()).Returns(501);
        _adoService.FetchAsync(501, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(501, "Child").Build());
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
        result.CycleErrors.ShouldBeEmpty();
        result.Results.Count.ShouldBe(2);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.Created);
        result.Results[1].Status.ShouldBe(SeedPublishStatus.Created);
        result.CreatedCount.ShouldBe(2);
        await _adoService.Received(2).CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: multiple orphaned parents — all reported
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_MultipleOrphanedParents_AllReported()
    {
        // Two seeds each referencing different non-existent parents
        var seed1 = new WorkItemBuilder(-1, "Orphan A").AsSeed(daysOld: 2).WithParent(-90).Build();
        var seed2 = new WorkItemBuilder(-2, "Orphan B").AsSeed(daysOld: 1).WithParent(-91).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed1, seed2 });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.PreFlightErrors.Count.ShouldBe(2);
        result.PreFlightErrors[0].ShouldContain("-90");
        result.PreFlightErrors[1].ShouldContain("-91");
        result.CycleErrors.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: dry run does not skip pre-flight validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_DryRun_PreFlightErrors_StillReportsErrors()
    {
        // Orphaned parent reference — pre-flight fires even in dryRun mode
        var seed = new WorkItemBuilder(-1, "DryOrphan").AsSeed(daysOld: 1).WithParent(-99).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync(dryRun: true);

        result.HasErrors.ShouldBeTrue();
        result.PreFlightErrors.Count.ShouldBe(1);
        result.PreFlightErrors[0].ShouldContain("-99");
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: self-referencing parent creates a cycle
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_SelfParentLoop_DetectedAsCycle_NoAdoCalls()
    {
        // Seed -1 references itself as parent → self-loop in dependency graph
        var seed = new WorkItemBuilder(-1, "SelfRef").AsSeed(daysOld: 1).WithParent(-1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var result = await _orchestrator.PublishAllAsync();

        result.HasErrors.ShouldBeTrue();
        result.CycleErrors.Count.ShouldBe(1);
        result.CycleErrors[0].ShouldContain("-1");
        result.Results.ShouldBeEmpty();
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pre-flight: force + dryRun batch — skips validation, returns DryRun
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PublishAllAsync_ForceAndDryRun_SkipsPreFlight_ReturnsDryRun()
    {
        // Invalid seed (empty title) would fail pre-flight validation
        // force=true skips batch pre-flight, dryRun prevents API calls → DryRun status
        var seed = new WorkItemBuilder(-1, "").AsSeed(daysOld: 1).Build();

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var result = await _orchestrator.PublishAllAsync(force: true, dryRun: true);

        result.HasErrors.ShouldBeFalse();
        result.PreFlightErrors.ShouldBeEmpty();
        result.CycleErrors.ShouldBeEmpty();
        result.Results.Count.ShouldBe(1);
        result.Results[0].Status.ShouldBe(SeedPublishStatus.DryRun);
        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }
}
