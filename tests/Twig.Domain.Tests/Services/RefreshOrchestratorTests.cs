using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class RefreshOrchestratorTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IIterationService _iterationService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly ITrackingService _trackingService;
    private readonly RefreshOrchestrator _orchestrator;

    public RefreshOrchestratorTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _iterationService = Substitute.For<IIterationService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        _trackingService = Substitute.For<ITrackingService>();

        _orchestrator = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService,
            _pendingChangeStore, _protectedCacheWriter, _workingSetService, _syncCoordinatorFactory,
            _iterationService,
            _trackingService);
    }

    // ── FetchItemsAsync tests ──────────────────────────────────────

    [Fact]
    public async Task FetchItems_NoResults_ReturnsZeroCount()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.ItemCount.ShouldBe(0);
        result.Conflicts.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchItems_WithResults_FetchesAndSaves()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        var item1 = new WorkItemBuilder(1, "Item 1").Build();
        var item2 = new WorkItemBuilder(2, "Item 2").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2 });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.ItemCount.ShouldBe(2);
        await _workItemRepo.Received().SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchItems_SkipsNegativeIds()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, -1 });
        var item = new WorkItemBuilder(1, "Real Item").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.ItemCount.ShouldBe(1);
        await _adoService.Received(1).FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchItems_FetchesActiveItemSeparately_WhenNotInBatch()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var sprintItem = new WorkItemBuilder(1, "Sprint").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var activeItem = new WorkItemBuilder(42, "Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(activeItem);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.ItemCount.ShouldBe(1);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchItems_ActiveItemInBatch_SkipsDuplicateFetch()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        var items = new[] { new WorkItemBuilder(1, "A").Build(), new WorkItemBuilder(2, "B").Build() };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        _adoService.FetchChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        await _adoService.DidNotReceive().FetchAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchItems_Force_UsesDirectSave()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = new WorkItemBuilder(1, "Item").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        await _orchestrator.FetchItemsAsync("SELECT ...", force: true);

        await _workItemRepo.Received().SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchItems_DetectsConflicts_WhenProtectedItemHasNewerRevision()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        // Remote item has revision 5
        var remote = new WorkItemBuilder(1, "Item").Build();
        remote.MarkSynced(5);
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remote });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Local item has revision 3 and is dirty (protected)
        var local = new WorkItemBuilder(1, "Item").Dirty().Build();
        local.MarkSynced(3);
        local.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>()).Returns(new[] { local });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(local);

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.Conflicts.Count.ShouldBe(1);
        result.Conflicts[0].Id.ShouldBe(1);
        result.Conflicts[0].LocalRevision.ShouldBe(3);
        result.Conflicts[0].RemoteRevision.ShouldBe(5);
    }

    [Fact]
    public async Task FetchItems_ActiveNotInBatch_FiresFetchAndChildrenConcurrently()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var sprintItem = new WorkItemBuilder(1, "Sprint").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Track call ordering to verify concurrency
        var callLog = new List<string>();
        var activeItem = new WorkItemBuilder(42, "Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                callLog.Add("FetchAsync-start");
                await Task.Yield();
                callLog.Add("FetchAsync-end");
                return activeItem;
            });

        var child = new WorkItemBuilder(100, "Child").Build();
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                callLog.Add("FetchChildrenAsync-start");
                await Task.Yield();
                callLog.Add("FetchChildrenAsync-end");
                return (IReadOnlyList<WorkItem>)new[] { child };
            });

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        // Both calls should have been initiated before either completed
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchChildrenAsync(42, Arg.Any<CancellationToken>());

        // FetchChildrenAsync should start before FetchAsync completes (concurrent)
        var childStart = callLog.IndexOf("FetchChildrenAsync-start");
        var fetchEnd = callLog.IndexOf("FetchAsync-end");
        childStart.ShouldBeLessThan(fetchEnd, "FetchChildrenAsync should start before FetchAsync completes");
    }

    // ── HydrateAncestorsAsync tests ─────────────────────────────────

    [Fact]
    public async Task HydrateAncestors_FetchesOrphanParents()
    {
        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<int>>(new[] { 5 })
                    : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            });

        var parent = new WorkItemBuilder(5, "Parent").Build();
        _adoService.FetchBatchAsync(Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        await _orchestrator.HydrateAncestorsAsync();

        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 5)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HydrateAncestors_CapsAt5Levels()
    {
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>(new[] { 999 }));
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(999, "Phantom").Build() });

        await _orchestrator.HydrateAncestorsAsync();

        await _workItemRepo.Received(5).GetOrphanParentIdsAsync(Arg.Any<CancellationToken>());
    }

    // ── Phantom dirty cleansing tests (#1335 / #1396) ───────────────

    [Fact]
    public async Task FetchItems_CallsClearPhantomDirtyFlags_BeforeSyncGuard()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = new WorkItemBuilder(1, "Item").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        await _workItemRepo.Received(1).ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task FetchItems_ReturnsPhantomsCleansedCount(int count)
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        var item = new WorkItemBuilder(1, "Item").Build();
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>()).Returns(count);

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.PhantomsCleansed.ShouldBe(count);
    }

    [Fact]
    public async Task FetchItems_NoResults_DoesNotCallClearPhantomDirty()
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await _orchestrator.FetchItemsAsync("SELECT ...", force: false);

        result.PhantomsCleansed.ShouldBe(0);
        await _workItemRepo.DidNotReceive().ClearPhantomDirtyFlagsAsync(Arg.Any<CancellationToken>());
    }

    // ── SyncTrackedTreesAsync tests ──────────────────────────────────

    [Fact]
    public async Task SyncTrackedTrees_DelegatesToTrackingService()
    {
        _trackingService.SyncTrackedTreesAsync(Arg.Any<SyncCoordinator>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var result = await _orchestrator.SyncTrackedTreesAsync();

        result.ShouldBe(3);
        await _trackingService.Received(1).SyncTrackedTreesAsync(
            Arg.Any<SyncCoordinator>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncTrackedTrees_NullTrackingService_ReturnsZero()
    {
        var orchestratorWithoutTracking = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService,
            _pendingChangeStore, _protectedCacheWriter, _workingSetService, _syncCoordinatorFactory,
            _iterationService,
            trackingService: null);

        var result = await orchestratorWithoutTracking.SyncTrackedTreesAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncTrackedTrees_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _trackingService.SyncTrackedTreesAsync(Arg.Any<SyncCoordinator>(), token)
            .Returns(0);

        await _orchestrator.SyncTrackedTreesAsync(token);

        await _trackingService.Received(1).SyncTrackedTreesAsync(
            Arg.Any<SyncCoordinator>(), token);
    }

    [Fact]
    public async Task SyncTrackedTrees_UsesReadWriteSyncCoordinator()
    {
        var receivedCoordinator = (SyncCoordinator?)null;
        _trackingService.SyncTrackedTreesAsync(Arg.Any<SyncCoordinator>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                receivedCoordinator = callInfo.Arg<SyncCoordinator>();
                return 0;
            });

        await _orchestrator.SyncTrackedTreesAsync();

        receivedCoordinator.ShouldNotBeNull();
        receivedCoordinator.ShouldBeSameAs(_syncCoordinatorFactory.ReadWrite);
    }

    // ── ApplyCleanupPolicyAsync tests ────────────────────────────────

    [Fact]
    public async Task ApplyCleanupPolicy_NonePolicy_ReturnsZeroWithoutCallingService()
    {
        var result = await _orchestrator.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.None);

        result.ShouldBe(0);
        await _trackingService.DidNotReceive().ApplyCleanupPolicyAsync(
            Arg.Any<TrackingCleanupPolicy>(), Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicy_NullTrackingService_ReturnsZero()
    {
        var orchestratorWithoutTracking = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService,
            _pendingChangeStore, _protectedCacheWriter, _workingSetService, _syncCoordinatorFactory,
            _iterationService,
            trackingService: null);

        var result = await orchestratorWithoutTracking.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnComplete);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task ApplyCleanupPolicy_OnComplete_DelegatesToTrackingService()
    {
        var expectedIteration = IterationPath.Parse("Project\\Sprint 1").Value;
        _trackingService.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, expectedIteration, Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await _orchestrator.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnComplete);

        result.ShouldBe(2);
        await _trackingService.Received(1).ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, expectedIteration, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicy_OnCompleteAndPast_DelegatesToTrackingService()
    {
        var expectedIteration = IterationPath.Parse("Project\\Sprint 1").Value;
        _trackingService.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, expectedIteration, Arg.Any<CancellationToken>())
            .Returns(5);

        var result = await _orchestrator.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnCompleteAndPast);

        result.ShouldBe(5);
        await _trackingService.Received(1).ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, expectedIteration, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicy_ResolvesCurrentIterationFromService()
    {
        var customIteration = IterationPath.Parse("MyProject\\Sprint 42").Value;
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(customIteration);
        _trackingService.ApplyCleanupPolicyAsync(
            Arg.Any<TrackingCleanupPolicy>(), customIteration, Arg.Any<CancellationToken>())
            .Returns(1);

        await _orchestrator.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnComplete);

        await _iterationService.Received(1).GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        await _trackingService.Received(1).ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, customIteration, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicy_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        _trackingService.ApplyCleanupPolicyAsync(
            Arg.Any<TrackingCleanupPolicy>(), Arg.Any<IterationPath>(), token)
            .Returns(0);

        await _orchestrator.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnComplete, token);

        await _iterationService.Received(1).GetCurrentIterationAsync(token);
        await _trackingService.Received(1).ApplyCleanupPolicyAsync(
            Arg.Any<TrackingCleanupPolicy>(), Arg.Any<IterationPath>(), token);
    }
}
