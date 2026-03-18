using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
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
        var item = MakeItem(42, lastSyncedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
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
        var staleItem = MakeItem(42, lastSyncedAt: DateTimeOffset.UtcNow.AddMinutes(-60));
        _workItemRepo.GetByIdAsync(42).Returns(staleItem);

        var fetched = MakeItem(42);
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
        var item = MakeItem(42, lastSyncedAt: null);
        _workItemRepo.GetByIdAsync(42).Returns(item);

        var fetched = MakeItem(42);
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

        var fetched = MakeItem(42);
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
        var staleItem = MakeItem(42, lastSyncedAt: DateTimeOffset.UtcNow.AddMinutes(-60));
        _workItemRepo.GetByIdAsync(42).Returns(staleItem);
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 42 });

        var fetched = MakeItem(42);
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
        var children = new[] { MakeItem(10), MakeItem(11), MakeItem(12) };
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

        var children = new[] { MakeItem(10), MakeItem(11), MakeItem(12) };
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
        var item = MakeItem(42, lastSyncedAt: DateTimeOffset.UtcNow.AddMinutes(-CacheStaleMinutes).AddSeconds(1));
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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: fresh));
        _workItemRepo.GetByIdAsync(11).Returns(MakeItem(11, lastSyncedAt: fresh));
        _workItemRepo.GetByIdAsync(12).Returns(MakeItem(12, lastSyncedAt: fresh));

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

        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: fresh));
        _workItemRepo.GetByIdAsync(11).Returns(MakeItem(11, lastSyncedAt: stale));
        _workItemRepo.GetByIdAsync(12).Returns(MakeItem(12, lastSyncedAt: stale));

        var fetched11 = MakeItem(11);
        var fetched12 = MakeItem(12);
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

        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: stale));
        _workItemRepo.GetByIdAsync(11).Returns(MakeItem(11, lastSyncedAt: stale));
        _workItemRepo.GetByIdAsync(12).Returns(MakeItem(12, lastSyncedAt: stale));

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(MakeItem(10));
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(MakeItem(11));
        _adoService.FetchAsync(12, Arg.Any<CancellationToken>()).Returns(MakeItem(12));

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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: null));
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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: stale));
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(MakeItem(10));

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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: stale));
        _workItemRepo.GetByIdAsync(11).Returns(MakeItem(11, lastSyncedAt: stale));

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(MakeItem(10));
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(MakeItem(11));

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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: stale));
        _workItemRepo.GetByIdAsync(11).Returns(MakeItem(11, lastSyncedAt: stale));

        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(MakeItem(10));
        _adoService.FetchAsync(11, Arg.Any<CancellationToken>()).Returns(MakeItem(11));

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
        _workItemRepo.GetByIdAsync(10).Returns(MakeItem(10, lastSyncedAt: null));
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
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(MakeItem(10));

        var ws = new WorkingSet { ActiveItemId = 10 };

        var result = await _sut.SyncWorkingSetAsync(ws);

        result.ShouldBeOfType<SyncResult.Updated>()
              .ChangedCount.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(int id, DateTimeOffset? lastSyncedAt = null) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = $"Item {id}",
        State = "Active",
        LastSyncedAt = lastSyncedAt,
    };
}
