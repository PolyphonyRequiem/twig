using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class ProtectedCacheWriterTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
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
        var items = new[] { MakeItem(1), MakeItem(2), MakeItem(3) };

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
        var dirtyItem = MakeItem(2);
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync().Returns(new[] { dirtyItem });

        var items = new[] { MakeItem(1), MakeItem(2), MakeItem(3) };

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

        var items = new[] { MakeItem(1), MakeItem(2) };

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
        var item = MakeItem(10);

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

        var item = MakeItem(10);

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
        var dirtyItem = MakeItem(5);
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync().Returns(new[] { dirtyItem });
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 8 });

        var items = new[] { MakeItem(3), MakeItem(5), MakeItem(7), MakeItem(8) };

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

        var items = new[] { MakeItem(1) };
        await _sut.SaveBatchProtectedAsync(items, cts.Token);

        await _workItemRepo.Received(1).GetDirtyItemsAsync(cts.Token);
        await _pendingStore.Received(1).GetDirtyItemIdsAsync(cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static WorkItem MakeItem(int id) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = $"Item {id}",
        State = "Active",
    };
}
