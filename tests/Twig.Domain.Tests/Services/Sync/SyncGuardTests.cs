using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Sync;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

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
            new WorkItemBuilder(10, "Item 10").Build(),
            new WorkItemBuilder(20, "Item 20").Build(),
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
        _repo.GetDirtyItemsAsync().Returns(new[] { new WorkItemBuilder(10, "Item 10").Build() });
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
            new WorkItemBuilder(10, "Item 10").Build(),
            new WorkItemBuilder(20, "Item 20").Build(),
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

}
