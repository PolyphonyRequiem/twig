using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

/// <summary>
/// Wayfinder 0004 §3: <b>reads stop fetching</b>. Staleness becomes an outcome the surface
/// interprets, not a policy the coordinator acts on.
/// </summary>
/// <remarks>
/// <para>
/// Every test here fails on the pre-slice code, and they fail in two opposite directions —
/// which is the point. The old <c>SyncCoordinator</c> conflated two questions into one method:
/// <i>"how fresh is the cache?"</i> and <i>"go make it fresh"</i>. Splitting them means:
/// </para>
/// <list type="bullet">
///   <item><description>the new <c>Read*</c> methods must NEVER reach the network, even when
///   the item is stale — on the old code these methods did not exist at all;</description></item>
///   <item><description>the <c>Sync*</c> methods must ALWAYS fetch when asked, even when the
///   cache is fresh — on the old code a fresh item short-circuited to <c>UpToDate</c> and the
///   caller silently got nothing.</description></item>
/// </list>
/// <para>
/// The second class is the one that matters for the user-visible break: <c>--refresh</c> is
/// worthless if an explicit refresh can still be swallowed by a staleness check.
/// </para>
/// <para>
/// <b>Fixture precondition.</b> Freshness is relative to <c>CacheStaleMinutes</c>, so a fixture
/// that forgets <c>LastSyncedAt</c> gets <c>null</c> — which reads as *stale*, not fresh, and
/// would quietly send a "fresh item" test down the stale path. Each test asserts its intended
/// side of the threshold explicitly rather than trusting the builder default.
/// </para>
/// </remarks>
public class ReadsDoNotFetchTests
{
    private const int CacheStaleMinutes = 30;

    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly SyncCoordinator _sut;

    public ReadsDoNotFetchTests()
    {
        _workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, _pendingStore);
        _sut = new SyncCoordinator(_workItemRepo, _adoService, protectedWriter, _pendingStore, CacheStaleMinutes);
    }

    private static WorkItem Fresh(int id) =>
        new WorkItemBuilder(id, $"Item {id}").InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-1)).Build();

    private static WorkItem StaleItem(int id, int minutesAgo = 120) =>
        new WorkItemBuilder(id, $"Item {id}").InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)).Build();

    /// <summary>
    /// Asserts the fixture actually sits on the intended side of the staleness threshold, so a
    /// future change to <see cref="CacheStaleMinutes"/> or the builder default cannot silently
    /// flip a test onto the branch it was written to exclude.
    /// </summary>
    private static void AssertIsStale(WorkItem item, bool expectedStale)
    {
        var actuallyStale = item.LastSyncedAt is null ||
            DateTimeOffset.UtcNow - item.LastSyncedAt.Value >= TimeSpan.FromMinutes(CacheStaleMinutes);
        actuallyStale.ShouldBe(expectedStale,
            $"fixture precondition: item #{item.Id} must be {(expectedStale ? "stale" : "fresh")} " +
            $"relative to CacheStaleMinutes={CacheStaleMinutes}, or this test exercises the wrong branch");
    }

    // ═══════════════════════════════════════════════════════════════
    //  A read reports staleness — and never fetches to resolve it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadItemAsync_StaleItem_ReturnsStaleAndDoesNotFetch()
    {
        var item = StaleItem(42);
        AssertIsStale(item, expectedStale: true);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _sut.ReadItemAsync(42);

        result.ShouldBeUnionCase<Stale>().LastSyncedAt.ShouldBe(item.LastSyncedAt);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadItemAsync_FreshItem_ReturnsUpToDateAndDoesNotFetch()
    {
        var item = Fresh(42);
        AssertIsStale(item, expectedStale: false);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _sut.ReadItemAsync(42);

        result.ShouldBeUnionCase<UpToDate>();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A cache miss is <see cref="NotCached"/>, not an implicit fetch. This is 0003 §4's
    /// silent-coercion rule: "I have no data" is reported, not papered over.
    /// </summary>
    [Fact]
    public async Task ReadItemAsync_MissingItem_ReturnsNotCachedAndDoesNotFetch()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _sut.ReadItemAsync(42);

        result.ShouldBeUnionCase<NotCached>().Id.ShouldBe(42);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A never-synced item is stale with a <c>null</c> timestamp — the surface must be able to
    /// tell "old" from "never", so this does not collapse into a fabricated date.
    /// </summary>
    [Fact]
    public async Task ReadItemAsync_NeverSynced_ReturnsStaleWithNullTimestamp()
    {
        var item = new WorkItemBuilder(42, "Item 42").InState("Active").LastSyncedAt(null).Build();
        AssertIsStale(item, expectedStale: true);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _sut.ReadItemAsync(42);

        result.ShouldBeUnionCase<Stale>().LastSyncedAt.ShouldBeNull();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadItemSetAsync_ReportsOldestStaleAndDoesNotFetch()
    {
        var older = StaleItem(1, minutesAgo: 600);
        var newer = StaleItem(2, minutesAgo: 90);
        AssertIsStale(older, expectedStale: true);
        AssertIsStale(newer, expectedStale: true);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(older);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(newer);

        var result = await _sut.ReadItemSetAsync([1, 2]);

        result.ShouldBeUnionCase<Stale>().LastSyncedAt.ShouldBe(older.LastSyncedAt);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A miss anywhere in the set outranks staleness — the caller cannot render what it does not have.</summary>
    [Fact]
    public async Task ReadItemSetAsync_AnyMissingItem_ReturnsNotCached()
    {
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(StaleItem(1));
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await _sut.ReadItemSetAsync([1, 2]);

        result.ShouldBeUnionCase<NotCached>().Id.ShouldBe(2);
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  An explicit refresh always fetches — freshness never swallows it
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The regression that makes <c>--refresh</c> meaningful. On the pre-slice code a fresh
    /// item short-circuited to <c>UpToDate</c> before the fetch, so a user who explicitly asked
    /// for fresh data silently got the cache back.
    /// </summary>
    [Fact]
    public async Task SyncItemAsync_FreshItem_StillFetches()
    {
        var item = Fresh(42);
        AssertIsStale(item, expectedStale: false);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fetched);

        var result = await _sut.SyncItemAsync(42);

        result.ShouldBeUnionCase<Updated>().ChangedCount.ShouldBe(1);
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncItemSetAsync_AllItemsFresh_StillFetches()
    {
        var item = Fresh(7);
        AssertIsStale(item, expectedStale: false);
        _workItemRepo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(item);

        var fetched = new WorkItemBuilder(7, "Item 7").InState("Active").Build();
        _adoService.FetchAsync(7, Arg.Any<CancellationToken>()).Returns(fetched);

        var result = await _sut.SyncItemSetAsync([7]);

        result.ShouldBeUnionCase<Updated>().ChangedCount.ShouldBe(1);
        await _adoService.Received(1).FetchAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncWorkingSetAsync_AllItemsFresh_StillFetches()
    {
        var item = Fresh(9);
        AssertIsStale(item, expectedStale: false);
        _workItemRepo.GetByIdAsync(9, Arg.Any<CancellationToken>()).Returns(item);

        var fetched = new WorkItemBuilder(9, "Item 9").InState("Active").Build();
        _adoService.FetchAsync(9, Arg.Any<CancellationToken>()).Returns(fetched);

        var workingSet = new WorkingSet { SprintItemIds = [9] };

        var result = await _sut.SyncWorkingSetAsync(workingSet);

        result.ShouldBeUnionCase<Updated>().ChangedCount.ShouldBe(1);
        await _adoService.Received(1).FetchAsync(9, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The staleness check is gone from the fetch path entirely, not merely relaxed: an explicit
    /// sync must not read the cache to decide whether to proceed. If a future change reinstates
    /// a "check first" step, this fails even if the fetch still happens to occur.
    /// </summary>
    [Fact]
    public async Task SyncItemAsync_DoesNotConsultTheCacheToDecideWhetherToFetch()
    {
        var fetched = new WorkItemBuilder(42, "Item 42").InState("Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fetched);

        await _sut.SyncItemAsync(42);

        await _workItemRepo.DidNotReceive().GetByIdAsync(42, Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
    }
}
