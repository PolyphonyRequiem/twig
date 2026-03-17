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
