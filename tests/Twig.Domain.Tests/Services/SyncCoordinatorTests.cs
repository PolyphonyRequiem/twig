using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

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
        _sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, CacheStaleMinutes);
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
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, linkRepo, CacheStaleMinutes);

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
        var sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, linkRepo, CacheStaleMinutes);

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

}
