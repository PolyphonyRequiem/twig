using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

/// <summary>
/// Tests for <see cref="SyncCoordinator.SyncLinksForSetAsync"/> — the plural sync added by
/// ADO #154.
/// </summary>
public class SyncCoordinatorSetLinksTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly SyncCoordinator _sut;

    public SyncCoordinatorSetLinksTests()
    {
        _workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        _sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, _linkRepo, 30);
    }

    private void AdoReturns(IReadOnlyList<WorkItem> items, IReadOnlyList<WorkItemLink> links)
    {
        _adoService.FetchBatchWithLinksAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns((items, links));
    }

    [Fact]
    public async Task SyncLinksForSetAsync_PersistsEachItemsOwnEdges()
    {
        var items = new[]
        {
            new WorkItemBuilder(10, "Ten").Build(),
            new WorkItemBuilder(20, "Twenty").Build(),
        };
        var links = new[]
        {
            new WorkItemLink(10, 100, LinkTypes.Predecessor),
            new WorkItemLink(20, 200, LinkTypes.Successor),
        };
        AdoReturns(items, links);

        var result = await _sut.SyncLinksForSetAsync([10, 20]);

        result.Count.ShouldBe(2);

        // Each id is written with ITS OWN edges — a mutant writing the whole set's edges to
        // every id would fail these predicates.
        await _linkRepo.Received(1).SaveLinksAsync(10,
            Arg.Is<IReadOnlyList<WorkItemLink>>(l => l.Count == 1 && l[0].TargetId == 100),
            Arg.Any<CancellationToken>());
        await _linkRepo.Received(1).SaveLinksAsync(20,
            Arg.Is<IReadOnlyList<WorkItemLink>>(l => l.Count == 1 && l[0].TargetId == 200),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 🔴 The contract that is easy to get wrong: <c>SaveLinksAsync</c> REPLACES a source's
    /// whole edge set, so an item whose last link was removed in ADO comes back with no links
    /// and must still be written — otherwise its stale edges survive in the cache forever.
    /// </summary>
    [Fact]
    public async Task SyncLinksForSetAsync_ItemWithNoEdges_IsStillWrittenSoStaleEdgesAreCleared()
    {
        var items = new[]
        {
            new WorkItemBuilder(10, "Ten").Build(),
            new WorkItemBuilder(20, "Twenty").Build(),
        };
        // 20 came back with NO links — it used to have some.
        AdoReturns(items, [new WorkItemLink(10, 100, LinkTypes.Related)]);

        await _sut.SyncLinksForSetAsync([10, 20]);

        await _linkRepo.Received(1).SaveLinksAsync(20,
            Arg.Is<IReadOnlyList<WorkItemLink>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksForSetAsync_PersistsEveryFetchedWorkItem()
    {
        var items = new[]
        {
            new WorkItemBuilder(10, "Ten").Build(),
            new WorkItemBuilder(20, "Twenty").Build(),
        };
        AdoReturns(items, []);

        await _sut.SyncLinksForSetAsync([10, 20]);

        await _workItemRepo.Received().SaveAsync(Arg.Is<WorkItem>(w => w.Id == 10), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Is<WorkItem>(w => w.Id == 20), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksForSetAsync_UsesOnePluralAdoCallNotOnePerId()
    {
        AdoReturns([new WorkItemBuilder(10, "Ten").Build()], []);

        await _sut.SyncLinksForSetAsync([10, 20, 30]);

        await _adoService.Received(1).FetchBatchWithLinksAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 3),
            Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksForSetAsync_EmptyInput_TouchesNothing()
    {
        var result = await _sut.SyncLinksForSetAsync([]);

        result.ShouldBeEmpty();
        await _adoService.DidNotReceive().FetchBatchWithLinksAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _linkRepo.DidNotReceive().SaveLinksAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncLinksForSetAsync_ReturnsTheWholeSetsEdges()
    {
        var items = new[] { new WorkItemBuilder(10, "Ten").Build(), new WorkItemBuilder(20, "Twenty").Build() };
        var links = new[]
        {
            new WorkItemLink(10, 100, LinkTypes.Predecessor),
            new WorkItemLink(10, 101, LinkTypes.Successor),
            new WorkItemLink(20, 200, LinkTypes.Related),
        };
        AdoReturns(items, links);

        var result = await _sut.SyncLinksForSetAsync([10, 20]);

        result.Count.ShouldBe(3);
        result.ShouldContain(l => l.SourceId == 20 && l.TargetId == 200);
    }
}
