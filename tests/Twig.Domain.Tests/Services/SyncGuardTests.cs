using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Services;

public class SyncGuardTests
{
    private readonly IWorkItemRepository _repo = Substitute.For<IWorkItemRepository>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();

    // ═══════════════════════════════════════════════════════════════
    //  Empty repositories — no protected IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_BothEmpty_ReturnsEmptySet()
    {
        _repo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var result = await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore);

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dirty items only — no pending changes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_DirtyOnly_ReturnsTheirIds()
    {
        _repo.GetDirtyItemsAsync().Returns(new[]
        {
            MakeItem(10),
            MakeItem(20),
        });
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var result = await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore);

        result.Count.ShouldBe(2);
        result.ShouldContain(10);
        result.ShouldContain(20);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes only — no dirty items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_PendingOnly_ReturnsTheirIds()
    {
        _repo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 30, 40 });

        var result = await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore);

        result.Count.ShouldBe(2);
        result.ShouldContain(30);
        result.ShouldContain(40);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Both dirty and pending — disjoint IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_BothSources_ReturnsUnion()
    {
        _repo.GetDirtyItemsAsync().Returns(new[] { MakeItem(10) });
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 20 });

        var result = await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore);

        result.Count.ShouldBe(2);
        result.ShouldContain(10);
        result.ShouldContain(20);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Overlapping IDs — no duplicates
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_OverlappingIds_ReturnsDistinctUnion()
    {
        _repo.GetDirtyItemsAsync().Returns(new[]
        {
            MakeItem(10),
            MakeItem(20),
        });
        _pendingStore.GetDirtyItemIdsAsync().Returns(new[] { 20, 30 });

        var result = await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore);

        result.Count.ShouldBe(3);
        result.ShouldContain(10);
        result.ShouldContain(20);
        result.ShouldContain(30);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CancellationToken forwarding
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetProtectedItemIdsAsync_ForwardsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        _repo.GetDirtyItemsAsync(cts.Token).Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync(cts.Token).Returns(Array.Empty<int>());

        await SyncGuard.GetProtectedItemIdsAsync(_repo, _pendingStore, cts.Token);

        await _repo.Received(1).GetDirtyItemsAsync(cts.Token);
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
        State = "New",
    };
}
