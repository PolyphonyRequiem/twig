using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ProtectedCacheWriterTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly ProtectedCacheWriter _sut;

    public ProtectedCacheWriterTests()
    {
        _sut = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        // Default: no protected items
        _workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveBatchProtectedAsync — all unprotected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_AllUnprotected_SavesAllReturnsNoSkipped()
    {
        var items = new[] { new WorkItemBuilder(1, "Item 1").InState("Active").Build(), new WorkItemBuilder(2, "Item 2").InState("Active").Build(), new WorkItemBuilder(3, "Item 3").InState("Active").Build() };

        var skipped = await _sut.SaveBatchProtectedAsync(items);

        skipped.ShouldBeEmpty();
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(x => x.Count() == 3),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveBatchProtectedAsync — some protected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_SomeProtected_SkipsProtectedSavesRest()
    {
        // Item 2 is dirty
        var dirtyItem = new WorkItemBuilder(2, "Item 2").InState("Active").Build();
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync().Returns(new[] { dirtyItem });

        var items = new[] { new WorkItemBuilder(1, "Item 1").InState("Active").Build(), new WorkItemBuilder(2, "Item 2").InState("Active").Build(), new WorkItemBuilder(3, "Item 3").InState("Active").Build() };

        var skipped = await _sut.SaveBatchProtectedAsync(items);

        skipped.Count.ShouldBe(1);
        skipped.ShouldContain(2);
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(x => x.Count() == 2),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveBatchProtectedAsync — all protected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_AllProtected_SavesNoneReturnsAllSkipped()
    {
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 1, 2 });

        var items = new[] { new WorkItemBuilder(1, "Item 1").InState("Active").Build(), new WorkItemBuilder(2, "Item 2").InState("Active").Build() };

        var skipped = await _sut.SaveBatchProtectedAsync(items);

        skipped.Count.ShouldBe(2);
        skipped.ShouldContain(1);
        skipped.ShouldContain(2);
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Any<IEnumerable<WorkItem>>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveBatchProtectedAsync — empty input
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_EmptyInput_ReturnsEmptySkipped()
    {
        var skipped = await _sut.SaveBatchProtectedAsync(Array.Empty<WorkItem>());

        skipped.ShouldBeEmpty();
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Any<IEnumerable<WorkItem>>(),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveProtectedAsync — unprotected item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveProtectedAsync_UnprotectedItem_SavesAndReturnsTrue()
    {
        var item = new WorkItemBuilder(10, "Item 10").InState("Active").Build();

        var saved = await _sut.SaveProtectedAsync(item);

        saved.ShouldBeTrue();
        await _workItemRepo.Received(1).SaveAsync(item, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveProtectedAsync — protected item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveProtectedAsync_ProtectedItem_SkipsAndReturnsFalse()
    {
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 10 });

        var item = new WorkItemBuilder(10, "Item 10").InState("Active").Build();

        var saved = await _sut.SaveProtectedAsync(item);

        saved.ShouldBeFalse();
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SaveBatchProtectedAsync — returns correct skipped IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_ReturnsCorrectSkippedIds()
    {
        // Dirty from repo and pending from store
        var dirtyItem = new WorkItemBuilder(5, "Item 5").InState("Active").Build();
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync().Returns(new[] { dirtyItem });
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 8 });

        var items = new[] { new WorkItemBuilder(3, "Item 3").InState("Active").Build(), new WorkItemBuilder(5, "Item 5").InState("Active").Build(), new WorkItemBuilder(7, "Item 7").InState("Active").Build(), new WorkItemBuilder(8, "Item 8").InState("Active").Build() };

        var skipped = await _sut.SaveBatchProtectedAsync(items);

        skipped.Count.ShouldBe(2);
        skipped.ShouldContain(5);
        skipped.ShouldContain(8);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CancellationToken forwarding
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SaveBatchProtectedAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _workItemRepo.GetDirtyItemsAsync(cts.Token).Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync(cts.Token).Returns(Array.Empty<int>());

        var items = new[] { new WorkItemBuilder(1, "Item 1").InState("Active").Build() };
        await _sut.SaveBatchProtectedAsync(items, cts.Token);

        await _workItemRepo.Received(1).GetDirtyItemsAsync(cts.Token);
        await _pendingStore.Received(1).GetDirtyItemIdsAsync(cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EPIC-002 Task 3: Concurrent save-during-sync race
    //  Start sync (FetchAsync with delay) → while fetch is in flight,
    //  call SaveAsync (making item dirty) → complete sync.
    //  Verify: ProtectedCacheWriter detects the dirty item and skips
    //  overwrite. The locally-saved version wins.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentSaveDuringSync_DirtyItemDetected_SyncSkipsOverwrite()
    {
        // Scenario: items 1–5 are being synced. During the fetch delay,
        // item 3 is saved locally (becomes dirty). When the sync completes,
        // ProtectedCacheWriter should detect item 3 as dirty and skip it.

        var itemDirtied = false;

        // SyncGuard checks dirty state at save time (not at fetch time).
        // When itemDirtied is true, item 3 appears in the dirty list.
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (itemDirtied)
                {
                    var dirtyItem = new WorkItemBuilder(3, "Item 3 LOCAL").InState("Active").Dirty().Build();
                    return new[] { dirtyItem };
                }
                return Array.Empty<WorkItem>();
            });

        // Simulate: FetchAsync has a delay, during which a concurrent save happens
        var fetchDelayTcs = new TaskCompletionSource();
        var allFetchedItems = new List<WorkItem>();

        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var id = callInfo.Arg<int>();

                // For item 3, wait for the concurrent save to happen
                if (id == 3)
                    await fetchDelayTcs.Task;

                return new WorkItemBuilder(id, $"Item {id} REMOTE").InState("Active").Build();
            });

        // 1. Start the sync (uses SyncCoordinator which calls ProtectedCacheWriter)
        var syncCoordinator = new SyncCoordinator(
            _workItemRepo, _adoService, _sut, cacheStaleMinutes: 30);

        var stale = DateTimeOffset.UtcNow.AddMinutes(-60);
        for (var i = 1; i <= 5; i++)
        {
            var id = i;
            _workItemRepo.GetByIdAsync(id).Returns(
                new WorkItemBuilder(id, $"Item {id}").InState("Active").LastSyncedAt(stale).Build());
        }

        var ws = new WorkingSet
        {
            ActiveItemId = 1,
            ChildrenIds = [2, 3, 4, 5],
        };

        var syncTask = syncCoordinator.SyncWorkingSetAsync(ws);

        // 2. While fetch for item 3 is in flight, simulate a concurrent save
        //    that makes item 3 dirty.
        await Task.Delay(50); // Give time for other fetches to start
        itemDirtied = true;

        // 3. Complete the delayed fetch for item 3
        fetchDelayTcs.SetResult();

        // 4. Wait for sync to complete
        var result = await syncTask;

        // 5. Verify: item 3 was skipped (protected as dirty), other 4 items saved
        result.ShouldBeOfType<SyncResult.Updated>();
        var updated = (SyncResult.Updated)result;
        updated.ChangedCount.ShouldBe(4); // 5 fetched - 1 skipped = 4 saved

        // Verify SaveBatchAsync was called with only 4 items (item 3 excluded)
        await _workItemRepo.Received(1).SaveBatchAsync(
            Arg.Is<IEnumerable<WorkItem>>(x => x.Count() == 4 && x.All(i => i.Id != 3)),
            Arg.Any<CancellationToken>());
    }

}
