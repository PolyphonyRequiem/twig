using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
/// AB#831 regression tests: a plain <c>twig sync</c> must leave the cache's edge table populated,
/// so a later cache-only read of an item with live Predecessor/Successor edges does not report
/// <c>links: []</c>.
/// </summary>
/// <remarks>
/// 🔴 The bug these pin was not a crash. <c>RefreshOrchestrator.FetchItemsAsync</c> filled
/// <c>work_items</c> and never wrote <c>work_item_links</c>, so a refreshed workspace reported an
/// EMPTY blocking graph that was byte-identical to the correct answer for a genuinely isolated
/// item. Two agent sessions acted on that false answer. The tests therefore assert on what reached
/// the LINK repository, because asserting only that the fetch happened is exactly the check that
/// passed for the whole life of the defect.
/// </remarks>
public class RefreshOrchestratorLinkPersistenceTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();
    private readonly RefreshOrchestrator _orchestrator;

    public RefreshOrchestratorLinkPersistenceTests()
    {
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, _linkRepo, 30, 30);

        _orchestrator = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService, _pendingChangeStore, protectedCacheWriter,
            workingSetService, syncCoordinatorFactory, _iterationService,
            trackingService: null, iterationCalendar: null, linkRepo: _linkRepo);
    }

    private void GivenBatch(IReadOnlyList<WorkItem> items, IReadOnlyList<WorkItemLink> links)
    {
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(items.Select(i => i.Id).ToArray());
        _adoService.FetchBatchWithLinksAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns((items, links));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
    }

    /// <summary>Captures the edge map handed to the repository across every batch write.</summary>
    private Dictionary<int, IReadOnlyList<WorkItemLink>> CapturedWrites()
    {
        var merged = new Dictionary<int, IReadOnlyList<WorkItemLink>>();
        foreach (var call in _linkRepo.ReceivedCalls()
                     .Where(c => c.GetMethodInfo().Name == nameof(IWorkItemLinkRepository.SaveLinksForSourcesAsync)))
        {
            var map = (IReadOnlyDictionary<int, IReadOnlyList<WorkItemLink>>)call.GetArguments()[0]!;
            foreach (var (id, links) in map)
                merged[id] = links;
        }

        return merged;
    }

    /// <summary>
    /// The headline regression. Before AB#831 this refresh saved two work items and zero edges.
    /// </summary>
    [Fact]
    public async Task FetchItems_ItemWithLiveEdges_PersistsThoseEdges()
    {
        var blocked = new WorkItemBuilder(742, "Blocked").Build();
        var blocker = new WorkItemBuilder(740, "Blocker").Build();
        GivenBatch([blocked, blocker], [new WorkItemLink(742, 740, LinkTypes.Predecessor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        var written = CapturedWrites();
        written.ShouldContainKey(742);
        written[742].Count.ShouldBe(1);
        written[742][0].TargetId.ShouldBe(740);
        written[742][0].LinkType.ShouldBe(LinkTypes.Predecessor);
    }

    /// <summary>
    /// The whole point of the change is that the edges ride the round trip the refresh was already
    /// paying for. An implementation that fetched links with a second pass would satisfy the test
    /// above and quietly double the cost of every sync, so pin the request shape too.
    /// </summary>
    [Fact]
    public async Task FetchItems_CostsNoExtraAdoRoundTrip_ForTheEdges()
    {
        var item = new WorkItemBuilder(742, "Blocked").Build();
        GivenBatch([item], [new WorkItemLink(742, 740, LinkTypes.Predecessor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        await _adoService.Received(1).FetchBatchWithLinksAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchWithLinksAsync(742, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 🔴 The edges are written in ONE transaction, not one per item. The cache runs WAL with
    /// SQLite's default <c>synchronous=FULL</c>, so every commit fsyncs: measured on a real disk,
    /// per-item writes across a 163-item refresh cost 370-700 ms against ~3 ms batched. A
    /// per-item loop is therefore a user-visible half-second regression on every sync, and it
    /// looks identical to this test's subject unless the call shape itself is pinned.
    /// </summary>
    [Fact]
    public async Task FetchItems_WritesEveryEdgeSetInOneBatch_NotOnePerItem()
    {
        var items = Enumerable.Range(1, 20).Select(i => new WorkItemBuilder(i, $"Item {i}").Build()).ToList();
        GivenBatch(items, [new WorkItemLink(1, 99, LinkTypes.Predecessor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        await _linkRepo.Received(1).SaveLinksForSourcesAsync(
            Arg.Is<IReadOnlyDictionary<int, IReadOnlyList<WorkItemLink>>>(m => m.Count == 20),
            Arg.Any<CancellationToken>());
        await _linkRepo.DidNotReceive().SaveLinksAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<WorkItemLink>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// 🔴 An id that came back with NO edges must still be written. Skipping it would leave the
    /// item unstamped, and an unstamped id reads back as "never verified" — the exact ambiguity
    /// this ticket exists to remove — as well as stranding stale edges after the last link on an
    /// item is removed in ADO.
    /// </summary>
    [Fact]
    public async Task FetchItems_ItemWithNoEdges_IsStillWrittenSoItReadsAsVerifiedEmpty()
    {
        var linked = new WorkItemBuilder(742, "Blocked").Build();
        var isolated = new WorkItemBuilder(900, "Isolated").Build();
        GivenBatch([linked, isolated], [new WorkItemLink(742, 740, LinkTypes.Predecessor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        var written = CapturedWrites();
        written.ShouldContainKey(900);
        written[900].ShouldBeEmpty();
    }

    /// <summary>
    /// Edges are bucketed by SOURCE id, not sprayed at every item in the batch.
    /// </summary>
    [Fact]
    public async Task FetchItems_EdgesAreBucketedBySourceId()
    {
        var a = new WorkItemBuilder(1, "A").Build();
        var b = new WorkItemBuilder(2, "B").Build();
        GivenBatch(
            [a, b],
            [new WorkItemLink(1, 99, LinkTypes.Predecessor), new WorkItemLink(2, 98, LinkTypes.Successor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        var written = CapturedWrites();
        written[1].Select(l => l.TargetId).ShouldBe([99]);
        written[2].Select(l => l.TargetId).ShouldBe([98]);
    }

    /// <summary>
    /// An edge whose source is outside the fetched set is not ours to write: writing it would
    /// replace that absent id's whole edge set from a partial view and stamp it verified.
    /// </summary>
    [Fact]
    public async Task FetchItems_EdgeFromAnUnfetchedSource_IsNotWritten()
    {
        var item = new WorkItemBuilder(1, "A").Build();
        GivenBatch([item], [new WorkItemLink(1, 99, LinkTypes.Predecessor), new WorkItemLink(555, 1, LinkTypes.Successor)]);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        var written = CapturedWrites();
        written.ShouldContainKey(1);
        written.ShouldNotContainKey(555);
    }

    /// <summary>
    /// The active work item is what <c>twig show</c> reads with no id, so its edge set is the one
    /// an agent is most likely to consult. When it falls outside the sprint batch it is fetched
    /// singly — with relations.
    /// </summary>
    [Fact]
    public async Task FetchItems_OutOfBatchActiveItem_HasItsEdgesPersistedToo()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint").Build();
        GivenBatch([sprintItem], []);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var active = new WorkItemBuilder(42, "Active").Build();
        _adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((active, (IReadOnlyList<WorkItemLink>)[new WorkItemLink(42, 41, LinkTypes.Predecessor)]));
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        await _orchestrator.FetchItemsAsync("SELECT ...");

        var written = CapturedWrites();
        written.ShouldContainKey(42);
        written[42].Select(l => l.TargetId).ShouldBe([41]);
    }

    /// <summary>
    /// A link-store failure must degrade the refresh to items-without-edges, never fail it — the
    /// item rows are already saved and useful. This matches how every other read path in the
    /// codebase treats the link store.
    /// </summary>
    [Fact]
    public async Task FetchItems_LinkStoreThrows_RefreshStillSucceeds()
    {
        var item = new WorkItemBuilder(742, "Blocked").Build();
        GivenBatch([item], [new WorkItemLink(742, 740, LinkTypes.Predecessor)]);
        _linkRepo.SaveLinksForSourcesAsync(
                Arg.Any<IReadOnlyDictionary<int, IReadOnlyList<WorkItemLink>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("link store unavailable"));

        var result = await _orchestrator.FetchItemsAsync("SELECT ...");

        result.ItemCount.ShouldBe(1);
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Children come from <c>FetchChildrenAsync</c>, which does not return relations. Stamping
    /// them would trade one false claim for another, so they are deliberately left unverified.
    /// </summary>
    [Fact]
    public async Task FetchItems_Children_AreNotClaimedAsVerified()
    {
        var sprintItem = new WorkItemBuilder(1, "Sprint").Build();
        GivenBatch([sprintItem], []);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(77, "Child").Build() });

        await _orchestrator.FetchItemsAsync("SELECT ...");

        CapturedWrites().ShouldNotContainKey(77);
    }
}
