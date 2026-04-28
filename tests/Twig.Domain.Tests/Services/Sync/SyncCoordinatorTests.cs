using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Sync;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

public class SyncCoordinatorTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly SyncCoordinator _sut;

    private const int CacheStaleMinutes = 30;

    public SyncCoordinatorTests()
    {
        // Default: no protected items
        _workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        _sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, CacheStaleMinutes);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — fresh item → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_FreshItem_ReturnsUpToDate()
    {
        var item = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-5)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — stale item → Updated
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_StaleItem_FetchesAndReturnsUpdated()
    {
        var staleItem = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(staleItem);

        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42).Returns(fetched);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
        await _workItemRepo.Received(1).SaveAsync(fetched, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — null LastSyncedAt → treated as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_NullLastSyncedAt_TreatedAsStale()
    {
        var item = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(null).Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42).Returns(fetched);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — item not in cache → treated as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_ItemNotInCache_FetchesAndReturnsUpdated()
    {
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);

        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42).Returns(fetched);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — stale protected item → Skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_StaleProtectedItem_ReturnsSkipped()
    {
        var staleItem = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(staleItem);
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 42 });

        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42).Returns(fetched);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Skipped>()
              .Reason.ShouldContain("pending");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — fetch failure → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_FetchFails_ReturnsFailed()
    {
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42).Throws(new HttpRequestException("Connection refused"));

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Failed>()
              .Reason.ShouldContain("Connection refused");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncChildrenAsync — always fetches (DD-15)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncChildrenAsync_AlwaysFetchesRegardlessOfStaleness()
    {
        var children = new[] { new WorkItemBuilder(10, "Item 10").InState("Active").Build(), new WorkItemBuilder(11, "Item 11").InState("Active").Build(), new WorkItemBuilder(12, "Item 12").InState("Active").Build() };
        _adoService.FetchChildrenAsync(1).Returns(children);

        var result = await _sut.SyncChildrenAsync(1);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(3);
        await _adoService.Received(1).FetchChildrenAsync(1, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncChildrenAsync — mixed protected/unprotected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncChildrenAsync_MixedProtected_ReturnsCorrectUpdatedCount()
    {
        // Item 11 is protected
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 11 });

        var children = new[] { new WorkItemBuilder(10, "Item 10").InState("Active").Build(), new WorkItemBuilder(11, "Item 11").InState("Active").Build(), new WorkItemBuilder(12, "Item 12").InState("Active").Build() };
        _adoService.FetchChildrenAsync(1).Returns(children);

        var result = await _sut.SyncChildrenAsync(1);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(2); // 3 fetched - 1 skipped = 2 saved
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncChildrenAsync — fetch failure → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncChildrenAsync_FetchFails_ReturnsFailed()
    {
        _adoService.FetchChildrenAsync(1).Throws(new HttpRequestException("Timeout"));

        var result = await _sut.SyncChildrenAsync(1);

        result.ShouldBeOfType<SyncResult.Failed>()
              .Reason.ShouldContain("Timeout");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — boundary: exactly at stale threshold → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_ExactlyAtThreshold_ReturnsUpToDate()
    {
        // LastSyncedAt is exactly cacheStaleMinutes - 1 second ago → still fresh
        var item = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-CacheStaleMinutes).AddSeconds(1)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.UpToDate>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cancellation propagation — OperationCanceledException is NOT swallowed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_CancellationDuringFetch_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(42).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncItemAsync(42));
    }

    [Fact]
    public async Task SyncChildrenAsync_CancellationDuringFetch_PropagatesException()
    {
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncChildrenAsync(1));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncChildrenAsync — empty children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncChildrenAsync_NoChildren_ReturnsUpdatedZero()
    {
        _adoService.FetchChildrenAsync(1).Returns(Array.Empty<WorkItem>());

        var result = await _sut.SyncChildrenAsync(1);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — empty working set → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_EmptyWorkingSet_ReturnsUpToDate()
    {
        var ws = new WorkingSet();

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — all fresh → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_AllFresh_ReturnsUpToDate()
    {
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-5);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(fresh).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(fresh).Build());
        _workItemRepo.GetByIdAsync(12).Returns(new WorkItemBuilder(12, "Item 12").InState("Active").LastSyncedAt(fresh).Build());

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11, 12],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — mix stale/fresh → Updated(staleCount)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_MixStaleFresh_ReturnsUpdatedWithStaleCount()
    {
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-5);
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(fresh).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(12).Returns(new WorkItemBuilder(12, "Item 12").InState("Active").LastSyncedAt(stale).Build());

        var fetched11 = new WorkItemBuilder(11, "Item 11").InState("Active").Build();
        var fetched12 = new WorkItemBuilder(12, "Item 12").InState("Active").Build();
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(fetched11);
        _adoService.FetchAsync(12, Arg.Any<CancellationToken>()).Returns(fetched12);

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11, 12],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(2);
        await _adoService.DidNotReceive().FetchAsync(10, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(11, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(12, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — all stale → Updated(allCount)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_AllStale_ReturnsUpdatedWithAllCount()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(12).Returns(new WorkItemBuilder(12, "Item 12").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());
        _adoService.FetchAsync(12, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(12, "Item 12").InState("Active").Build());

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11, 12],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — network failure → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_FetchFails_ReturnsFailed()
    {
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(null).Build());
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var ws = new WorkingSet { ActiveItemId = 10 };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Failed>()
              .Reason.ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — seed IDs (negative) skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_SeedIdsSkipped_OnlyPositiveIdsSynced()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            SeedIds = [-1, -2],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
        await _adoService.DidNotReceive().FetchAsync(-1, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(-2, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — only seed IDs → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_OnlySeedIds_ReturnsUpToDate()
    {
        var ws = new WorkingSet { SeedIds = [-1, -2, -3] };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — dirty items skipped by ProtectedCacheWriter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_DirtyItemsSkippedByProtectedWriter_ReturnsUpdatedWithSavedCount()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());

        // Item 11 is dirty — ProtectedCacheWriter will skip it
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 11 });

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        // 2 fetched, 1 skipped = 1 saved; still Updated because items were fetched
        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — all stale items dirty → UpToDate (pre-filtered)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_AllStaleDirty_PreFiltered_ReturnsUpToDate()
    {
        // All candidates are in DirtyItemIds → pre-filtered out, no ADO fetch
        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11],
            DirtyItemIds = new HashSet<int> { 10, 11 },
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — all stale protected by pending store → Updated(0)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_AllStaleProtectedByPendingStore_ReturnsUpdatedZero()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());

        // Items are protected by pending store (not in DirtyItemIds) — SaveBatchProtectedAsync skips all
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 10, 11 });

        var ws = new WorkingSet
        {
            ActiveItemId = 10,
            ChildrenIds = [11],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — cancellation propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_Cancellation_PropagatesException()
    {
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(null).Build());
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var ws = new WorkingSet { ActiveItemId = 10 };

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncWorkingSetAsync(ws));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncWorkingSetAsync — items not in cache treated as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_ItemNotInCache_TreatedAsStale()
    {
        _workItemRepo.GetByIdAsync(10).Returns((WorkItem?)null);
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());

        var ws = new WorkingSet { ActiveItemId = 10 };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 1: Batch fetch partial failure
    //  20 items stale, FetchAsync succeeds for 18, throws for 2.
    //  Verify: 18 saved, 2 reported as failed, no data loss.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_PartialFetchFailure_SavesSuccessfulReportsFailures()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        // Set up 20 stale items
        for (var i = 1; i <= 20; i++)
        {
            var id = i;
            _workItemRepo.GetByIdAsync(id).Returns(
                new WorkItemBuilder(id, $"Item {id}").InState("Active").LastSyncedAt(stale).Build());
        }

        // Items 1–18 succeed, items 19–20 throw
        for (var i = 1; i <= 18; i++)
        {
            var id = i;
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>())
                .Returns(new WorkItemBuilder(id, $"Item {id}").InState("Active").Build());
        }
        _adoService.FetchAsync(19, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection reset"));

        var ws = new WorkingSet
        {
            ActiveItemId = 1,
            ChildrenIds = Enumerable.Range(2, 19).ToList(),
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        var partial = result.ShouldBeOfType<SyncResult.PartiallyUpdated>();
        partial.SavedCount.ShouldBe(18);
        partial.Failures.Count.ShouldBe(2);
        partial.Failures.ShouldContain(f => f.Id == 19);
        partial.Failures.ShouldContain(f => f.Id == 20);

        // 18 successful items were saved in a batch
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(x => x.Count() == 18),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 2: ADO rate-limit during batch
    //  FetchAsync throws for items 5+. Verify: items 1–4 saved,
    //  rest reported as failed, no partial corruption.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_RateLimitMidBatch_SavesSuccessfulReportsRateLimited()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        // Set up 10 stale items
        for (var i = 1; i <= 10; i++)
        {
            var id = i;
            _workItemRepo.GetByIdAsync(id).Returns(
                new WorkItemBuilder(id, $"Item {id}").InState("Active").LastSyncedAt(stale).Build());
        }

        // Items 1–4 succeed, items 5–10 throw rate-limit exception
        for (var i = 1; i <= 4; i++)
        {
            var id = i;
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>())
                .Returns(new WorkItemBuilder(id, $"Item {id}").InState("Active").Build());
        }
        for (var i = 5; i <= 10; i++)
        {
            var id = i;
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException("Rate limited. Retry after 30s."));
        }

        var ws = new WorkingSet
        {
            ActiveItemId = 1,
            ChildrenIds = Enumerable.Range(2, 9).ToList(),
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        var partial = result.ShouldBeOfType<SyncResult.PartiallyUpdated>();
        partial.SavedCount.ShouldBe(4);
        partial.Failures.Count.ShouldBe(6);

        // Items 5–10 are all reported as failures
        for (var i = 5; i <= 10; i++)
            partial.Failures.ShouldContain(f => f.Id == i);

        // Only the 4 successful items were saved, not the failed ones
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(x => x.Count() == 4),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 4: Concurrent dual-sync overlap
    //  Two SyncWorkingSetAsync calls with overlapping item sets
    //  {1–10} and {5–15}. Verify: no duplicate saves for 5–10,
    //  final cache state is consistent.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_DualSyncOverlap_BothComplete_OverlapFetchedTwice()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        // Set up items 1–15 as stale
        for (var i = 1; i <= 15; i++)
        {
            var id = i;
            _workItemRepo.GetByIdAsync(id).Returns(
                new WorkItemBuilder(id, $"Item {id}").InState("Active").LastSyncedAt(stale).Build());
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>())
                .Returns(new WorkItemBuilder(id, $"Item {id}").InState("Active").Build());
        }

        var ws1 = new WorkingSet
        {
            ActiveItemId = 1,
            ChildrenIds = Enumerable.Range(2, 9).ToList(), // IDs 2–10
        };
        var ws2 = new WorkingSet
        {
            ActiveItemId = 5,
            ChildrenIds = Enumerable.Range(6, 10).ToList(), // IDs 6–15
        };

        // Run both syncs concurrently
        var task1 = _sut.SyncWorkingSetAsync(ws1);
        var task2 = _sut.SyncWorkingSetAsync(ws2);
        var results = await Task.WhenAll(task1, task2);

        // Both should return Updated (no crash, no exception)
        results[0].ShouldBeOfType<SyncResult.Updated>();
        results[1].ShouldBeOfType<SyncResult.Updated>();

        // Verify: overlapping items 5–10 were fetched by both syncs (concurrent independent fetches)
        // Each sync fetches its own set independently
        for (var i = 5; i <= 10; i++)
        {
            await _adoService.Received(2).FetchAsync(i, Arg.Any<CancellationToken>());
        }

        // Non-overlapping items fetched exactly once each
        for (var i = 1; i <= 4; i++)
            await _adoService.Received(1).FetchAsync(i, Arg.Any<CancellationToken>());
        for (var i = 11; i <= 15; i++)
            await _adoService.Received(1).FetchAsync(i, Arg.Any<CancellationToken>());

        // Both SaveBatchAsync calls succeeded (two separate batches)
        await _workItemRepo.Received(2).SaveBatchAsync(
            Arg.Any<IEnumerable<WorkItem>>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002: All fetches fail edge case
    //  Every FetchAsync call throws. Verify: returns SyncResult.Failed
    //  (not PartiallyUpdated with 0 saved), no SaveBatchAsync call.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncWorkingSetAsync_AllFetchesFail_ReturnsFailed_NotPartiallyUpdated()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);

        // Set up 5 stale items
        for (var i = 1; i <= 5; i++)
        {
            var id = i;
            _workItemRepo.GetByIdAsync(id).Returns(
                new WorkItemBuilder(id, $"Item {id}").InState("Active").LastSyncedAt(stale).Build());
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>())
                .ThrowsAsync(new HttpRequestException($"Service unavailable for item {id}"));
        }

        var ws = new WorkingSet
        {
            ActiveItemId = 1,
            ChildrenIds = [2, 3, 4, 5],
        };

        var result = await _sut.SyncWorkingSetAsync(ws);

        // All fetches failed → should be Failed, NOT PartiallyUpdated(0, ...)
        var failed = result.ShouldBeOfType<SyncResult.Failed>();
        failed.Reason.ShouldContain("#1:");
        failed.Reason.ShouldContain("#5:");

        // No items were saved since all fetches failed
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Any<IEnumerable<WorkItem>>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncLinksAsync — fetches, persists, and returns links
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncLinksAsync_FetchesLinksAndPersistsAndReturns()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var fetchedItem = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 200, "Predecessor"),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetchedItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        var result = await sut.SyncLinksAsync(42);

        result.Count.ShouldBe(2);
        result[0].TargetId.ShouldBe(100);
        result[1].TargetId.ShouldBe(200);

        // Verify links were persisted
        await linkRepo.Received(1).SaveLinksAsync(42, Arg.Any<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(), Arg.Any<CancellationToken>());

        // Verify work item was saved via ProtectedCacheWriter
        await _workItemRepo.Received().SaveAsync(Arg.Is<WorkItem>(w => w.Id == 42), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksAsync_EmptyLinks_ReturnsEmptyList()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var fetchedItem = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetchedItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)Array.Empty<Domain.ValueObjects.WorkItemLink>()));

        var result = await sut.SyncLinksAsync(42);

        result.Count.ShouldBe(0);
        await linkRepo.Received(1).SaveLinksAsync(42, Arg.Any<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksAsync_WithoutLinkRepo_StillReturnsLinks()
    {
        // Uses the 4-parameter constructor (no link repo)
        var fetchedItem = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, "Related"),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetchedItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        var result = await _sut.SyncLinksAsync(42);

        result.Count.ShouldBe(1);
        result[0].TargetId.ShouldBe(100);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — empty list → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_EmptyList_ReturnsUpToDate()
    {
        var result = await _sut.SyncItemSetAsync(Array.Empty<int>());

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — only negative IDs (seeds) → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_OnlyNegativeIds_ReturnsUpToDate()
    {
        var result = await _sut.SyncItemSetAsync(new[] { -1, -2, -3 });

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — all fresh → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_AllFresh_ReturnsUpToDate()
    {
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-5);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(fresh).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(fresh).Build());

        var result = await _sut.SyncItemSetAsync(new[] { 10, 11 });

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — all stale → Updated(count)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_AllStale_ReturnsUpdated()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());

        var result = await _sut.SyncItemSetAsync(new[] { 10, 11 });

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — mixed fresh/stale → only fetches stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_MixedFreshStale_OnlyFetchesStale()
    {
        var fresh = DateTimeOffset.UtcNow.AddMinutes(-5);
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(fresh).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());

        var result = await _sut.SyncItemSetAsync(new[] { 10, 11 });

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
        await _adoService.DidNotReceive().FetchAsync(10, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(11, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — negative IDs filtered, positive synced
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_NegativeIdsFiltered_PositiveIdsSynced()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());

        var result = await _sut.SyncItemSetAsync(new[] { -1, 10, -2 });

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
        await _adoService.DidNotReceive().FetchAsync(-1, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(-2, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — partial fetch failure → PartiallyUpdated
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_PartialFetchFailure_ReturnsPartiallyUpdated()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Timeout"));

        var result = await _sut.SyncItemSetAsync(new[] { 10, 11 });

        var partial = result.ShouldBeOfType<SyncResult.PartiallyUpdated>();
        partial.SavedCount.ShouldBe(1);
        partial.Failures.Count.ShouldBe(1);
        partial.Failures[0].Id.ShouldBe(11);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — all fetches fail → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_AllFetchesFail_ReturnsFailed()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await _sut.SyncItemSetAsync(new[] { 10 });

        result.ShouldBeOfType<SyncResult.Failed>()
              .Reason.ShouldContain("Connection refused");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — items not in cache → treated as stale
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_ItemNotInCache_TreatedAsStale()
    {
        _workItemRepo.GetByIdAsync(10).Returns((WorkItem?)null);

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());

        var result = await _sut.SyncItemSetAsync(new[] { 10 });

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — dirty protection at write time via ProtectedCacheWriter
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_DirtyItemsSkippedByProtectedWriter_ReturnsUpdatedWithSavedCount()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(11).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").LastSyncedAt(stale).Build());

        // Item 11 is dirty — ProtectedCacheWriter will skip it at save time
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 11 });

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").Build());
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(11, "Item 11").InState("Active").Build());

        var result = await _sut.SyncItemSetAsync(new[] { 10, 11 });

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1); // 2 fetched - 1 skipped = 1 saved
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — cancellation propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_Cancellation_PropagatesException()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Item 10").InState("Active").LastSyncedAt(stale).Build());
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncItemSetAsync(new[] { 10 }));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — deleted item (not found) → evicted from cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_NotFound_EvictsFromCache()
    {
        var stale = new WorkItemBuilder(99, "Deleted").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(99).Returns(stale);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 99 not found."));

        var result = await _sut.SyncItemAsync(99);

        result.ShouldBeOfType<SyncResult.Failed>();
        await _workItemRepo.Received(1).DeleteByIdAsync(99, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — deleted items evicted, others still saved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_NotFoundItems_EvictedFromCache()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Ok").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(20).Returns(new WorkItemBuilder(20, "Deleted").InState("Active").LastSyncedAt(stale).Build());

        var fetched10 = new WorkItemBuilder(10, "Ok").InState("Active").Build();
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(fetched10);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 20 not found."));

        var result = await _sut.SyncItemSetAsync(new[] { 10, 20 });

        // Item 20 should be evicted
        await _workItemRepo.Received(1).DeleteByIdAsync(20, Arg.Any<CancellationToken>());
        // Item 10 should be saved via batch (ProtectedCacheWriter uses SaveBatchAsync)
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(items => items.Any(i => i.Id == 10)),
            Arg.Any<CancellationToken>());
        // Not-found failures should not appear in the failure list
        result.ShouldBeOfType<SyncResult.Updated>();
    }

    [Fact]
    public async Task SyncItemAsync_TransientError_DoesNotEvict()
    {
        var stale = new WorkItemBuilder(42, "Item").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(stale);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network timeout"));

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeOfType<SyncResult.Failed>();
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — not found eviction clears pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_NotFound_ClearsPendingChangesBeforeEviction()
    {
        var stale = new WorkItemBuilder(99, "Deleted").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(99).Returns(stale);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 99 not found."));

        await _sut.SyncItemAsync(99);

        // ClearChangesAsync must be called before DeleteByIdAsync
        Received.InOrder(() =>
        {
            _pendingStore.ClearChangesAsync(99, Arg.Any<CancellationToken>());
            _workItemRepo.DeleteByIdAsync(99, Arg.Any<CancellationToken>());
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemAsync — transient error does NOT clear pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemAsync_TransientError_DoesNotClearPendingChanges()
    {
        var stale = new WorkItemBuilder(42, "Item").InState("Active").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-60)).Build();
        _workItemRepo.GetByIdAsync(42).Returns(stale);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network timeout"));

        await _sut.SyncItemAsync(42);

        await _pendingStore.DidNotReceive().ClearChangesAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncItemSetAsync — not found eviction clears pending changes (batch)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_NotFoundItems_ClearsPendingChangesBeforeEviction()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Ok").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(20).Returns(new WorkItemBuilder(20, "Deleted").InState("Active").LastSyncedAt(stale).Build());

        var fetched10 = new WorkItemBuilder(10, "Ok").InState("Active").Build();
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(fetched10);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 20 not found."));

        await _sut.SyncItemSetAsync(new[] { 10, 20 });

        Received.InOrder(() =>
        {
            _pendingStore.ClearChangesAsync(20, Arg.Any<CancellationToken>());
            _workItemRepo.DeleteByIdAsync(20, Arg.Any<CancellationToken>());
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Batch eviction — multiple not-found IDs each get pending
    //  changes cleared before deletion (T-1365.2 / #1372)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_MultipleNotFoundItems_ClearsPendingChangesBeforeEvictionForEach()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(30).Returns(new WorkItemBuilder(30, "Gone A").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(40).Returns(new WorkItemBuilder(40, "Gone B").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(50).Returns(new WorkItemBuilder(50, "Gone C").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(30, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 30 not found."));
        _adoService.FetchAsync(40, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 40 not found."));
        _adoService.FetchAsync(50, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 50 not found."));

        await _sut.SyncItemSetAsync(new[] { 30, 40, 50 });

        await _pendingStore.Received(1).ClearChangesAsync(30, Arg.Any<CancellationToken>());
        await _pendingStore.Received(1).ClearChangesAsync(40, Arg.Any<CancellationToken>());
        await _pendingStore.Received(1).ClearChangesAsync(50, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(30, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(40, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(50, Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            _pendingStore.ClearChangesAsync(30, Arg.Any<CancellationToken>());
            _workItemRepo.DeleteByIdAsync(30, Arg.Any<CancellationToken>());
        });
        Received.InOrder(() =>
        {
            _pendingStore.ClearChangesAsync(40, Arg.Any<CancellationToken>());
            _workItemRepo.DeleteByIdAsync(40, Arg.Any<CancellationToken>());
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Batch negative — transient errors in batch do NOT clear
    //  pending changes; only not-found errors trigger cleanup
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncItemSetAsync_TransientError_DoesNotClearPendingChanges()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(80).Returns(new WorkItemBuilder(80, "Timeout").InState("Active").LastSyncedAt(stale).Build());

        _adoService.FetchAsync(80, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Request timed out"));

        var result = await _sut.SyncItemSetAsync(new[] { 80 });

        result.ShouldBeOfType<SyncResult.Failed>();
        await _pendingStore.DidNotReceive().ClearChangesAsync(80, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(80, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemSetAsync_MixedNotFoundAndTransient_OnlyClearsPendingChangesForNotFound()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        _workItemRepo.GetByIdAsync(10).Returns(new WorkItemBuilder(10, "Ok").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(20).Returns(new WorkItemBuilder(20, "Deleted").InState("Active").LastSyncedAt(stale).Build());
        _workItemRepo.GetByIdAsync(30).Returns(new WorkItemBuilder(30, "Timeout").InState("Active").LastSyncedAt(stale).Build());

        var fetched10 = new WorkItemBuilder(10, "Ok").InState("Active").Build();
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(fetched10);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Work item 20 not found."));
        _adoService.FetchAsync(30, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        await _sut.SyncItemSetAsync(new[] { 10, 20, 30 });

        // Not-found item 20: pending changes cleared, then evicted
        await _pendingStore.Received(1).ClearChangesAsync(20, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(20, Arg.Any<CancellationToken>());

        // Transient-error item 30: no pending change cleanup, no eviction
        await _pendingStore.DidNotReceive().ClearChangesAsync(30, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(30, Arg.Any<CancellationToken>());

        // Healthy item 10: no pending change cleanup, no eviction
        await _pendingStore.DidNotReceive().ClearChangesAsync(10, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().DeleteByIdAsync(10, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — root in cache with two ancestors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_RootInCacheWithTwoAncestors_ReturnUpdatedTwo()
    {
        // Root (100) → parent (200) → grandparent (300, no parent)
        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        var parent = new WorkItemBuilder(200, "Parent").InState("Active").WithParent(300).Build();
        var grandparent = new WorkItemBuilder(300, "Grandparent").InState("Active").Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(grandparent);

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(2);
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(300, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — root in cache with no parent → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_RootInCacheNoParent_ReturnsUpToDate()
    {
        var root = new WorkItemBuilder(100, "Root").InState("Active").Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — root not in cache → fetches root + parents
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_RootNotInCache_FetchesRootAndParents()
    {
        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);

        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        var parent = new WorkItemBuilder(200, "Parent").InState("Active").Build();
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(root);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(2); // root + parent
        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — root not in cache, no parent → Updated(1)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_RootNotInCacheNoParent_ReturnsUpdatedOne()
    {
        _workItemRepo.GetByIdAsync(100).Returns((WorkItem?)null);

        var root = new WorkItemBuilder(100, "Root").InState("Active").Build();
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(root);

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — cycle detection prevents infinite loop
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_CycleInParentChain_StopsAtCycle()
    {
        // 100 → 200 → 100 (cycle)
        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        var parent = new WorkItemBuilder(200, "Parent").InState("Active").WithParent(100).Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1); // only parent 200, stops before re-visiting 100
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — fetch failure → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_FetchFails_ReturnsFailed()
    {
        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        _adoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Failed>()
              .Reason.ShouldContain("Connection refused");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — cancellation propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_Cancellation_PropagatesException()
    {
        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        _adoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncParentChainAsync(100));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncParentChainAsync — protected items skipped
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncParentChainAsync_ProtectedParent_SkippedByWriter()
    {
        var root = new WorkItemBuilder(100, "Root").InState("Active").WithParent(200).Build();
        _workItemRepo.GetByIdAsync(100).Returns(root);

        var parent = new WorkItemBuilder(200, "Parent").InState("Active").Build();
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(parent);

        // Parent 200 is protected
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { 200 });

        var result = await _sut.SyncParentChainAsync(100);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(0); // fetched but skipped by writer
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — no links → UpToDate
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_NoLinks_ReturnsUpToDate()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var fetchedItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetchedItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)Array.Empty<Domain.ValueObjects.WorkItemLink>()));

        var result = await sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.UpToDate>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — links exist → fetches targets
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_WithLinks_FetchesTargetsAndReturnsUpdated()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var rootItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 200, "Predecessor"),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((rootItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        // Targets are stale (not in cache)
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(100, "Target A").InState("Active").Build());
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(200, "Target B").InState("Active").Build());

        var result = await sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>().ChangedCount.ShouldBe(2);
        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — duplicate target IDs → deduplicates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_DuplicateTargetIds_Deduplicates()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var rootItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 100, "Predecessor"), // same target, different link type
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((rootItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(100, "Target").InState("Active").Build());

        var result = await sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>().ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — FetchWithLinks fails → Failed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_FetchWithLinksFails_ReturnsFailed()
    {
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var result = await _sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Failed>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — cancellation → propagates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_Cancellation_PropagatesException()
    {
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => _sut.SyncRootLinksAsync(42));
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncRootLinksAsync — without link repo → still works
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncRootLinksAsync_WithoutLinkRepo_StillFetchesTargets()
    {
        var rootItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, "Related"),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((rootItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(100, "Target").InState("Active").Build());

        var result = await _sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>().ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncLinksAsync — link metadata values are persisted exactly
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncLinksAsync_PersistsExactLinkMetadata_SourceTargetAndType()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        IReadOnlyList<Domain.ValueObjects.WorkItemLink>? savedLinks = null;
        linkRepo.SaveLinksAsync(Arg.Any<int>(),
            Arg.Do<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(l => savedLinks = l),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var fetchedItem = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, Domain.ValueObjects.LinkTypes.Related),
            new(42, 200, Domain.ValueObjects.LinkTypes.Predecessor),
            new(42, 300, Domain.ValueObjects.LinkTypes.Successor),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetchedItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        await sut.SyncLinksAsync(42);

        savedLinks.ShouldNotBeNull();
        savedLinks.Count.ShouldBe(3);

        // Verify each link's full metadata: source_id, target_id, link_type
        savedLinks.ShouldContain(l => l.SourceId == 42 && l.TargetId == 100 && l.LinkType == Domain.ValueObjects.LinkTypes.Related);
        savedLinks.ShouldContain(l => l.SourceId == 42 && l.TargetId == 200 && l.LinkType == Domain.ValueObjects.LinkTypes.Predecessor);
        savedLinks.ShouldContain(l => l.SourceId == 42 && l.TargetId == 300 && l.LinkType == Domain.ValueObjects.LinkTypes.Successor);
    }

    [Fact]
    public async Task SyncRootLinksAsync_PersistsLinkMetadataBeforeFetchingTargets()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var rootItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        var targetItem = new WorkItemBuilder(100, "Target").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, Domain.ValueObjects.LinkTypes.Related),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((rootItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(targetItem);

        var result = await sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>();

        // Link metadata was persisted
        await linkRepo.Received(1).SaveLinksAsync(42,
            Arg.Is<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(l =>
                l.Count == 1 &&
                l[0].SourceId == 42 &&
                l[0].TargetId == 100 &&
                l[0].LinkType == Domain.ValueObjects.LinkTypes.Related),
            Arg.Any<CancellationToken>());

        // Target was also fetched and saved
        await _adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncRootLinksAsync_MultipleLinks_AllMetadataPersisted()
    {
        var linkRepo = Substitute.For<IWorkItemLinkRepository>();
        IReadOnlyList<Domain.ValueObjects.WorkItemLink>? savedLinks = null;
        linkRepo.SaveLinksAsync(Arg.Any<int>(),
            Arg.Do<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(l => savedLinks = l),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, linkRepo, CacheStaleMinutes);

        var rootItem = new WorkItemBuilder(42, "Root").InState("Active").Build();
        var links = new List<Domain.ValueObjects.WorkItemLink>
        {
            new(42, 100, Domain.ValueObjects.LinkTypes.Related),
            new(42, 200, Domain.ValueObjects.LinkTypes.Predecessor),
            new(42, 300, Domain.ValueObjects.LinkTypes.Successor),
        };
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((rootItem, (IReadOnlyList<Domain.ValueObjects.WorkItemLink>)links));

        // All targets are stale (not in cache)
        _workItemRepo.GetByIdAsync(Arg.Is<int>(id => id == 100 || id == 200 || id == 300), Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(100, "T1").InState("Active").Build());
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(200, "T2").InState("Active").Build());
        _adoService.FetchAsync(300, Arg.Any<CancellationToken>()).Returns(new WorkItemBuilder(300, "T3").InState("Active").Build());

        var result = await sut.SyncRootLinksAsync(42);

        result.ShouldBeOfType<SyncResult.Updated>().ChangedCount.ShouldBe(3);

        // Verify all three link metadata entries were persisted
        await linkRepo.Received(1).SaveLinksAsync(42,
            Arg.Is<IReadOnlyList<Domain.ValueObjects.WorkItemLink>>(l => l.Count == 3),
            Arg.Any<CancellationToken>());

        savedLinks.ShouldNotBeNull();
        savedLinks.ShouldContain(l => l.TargetId == 100 && l.LinkType == Domain.ValueObjects.LinkTypes.Related);
        savedLinks.ShouldContain(l => l.TargetId == 200 && l.LinkType == Domain.ValueObjects.LinkTypes.Predecessor);
        savedLinks.ShouldContain(l => l.TargetId == 300 && l.LinkType == Domain.ValueObjects.LinkTypes.Successor);
    }

}
