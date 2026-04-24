using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class TrackingServiceTests
{
    private readonly ITrackingRepository _repository = Substitute.For<ITrackingRepository>();

    private TrackingService CreateSut() => new(_repository);

    // ═══════════════════════════════════════════════════════════════
    //  TrackAsync
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(TrackingMode.Single)]
    [InlineData(TrackingMode.Tree)]
    public async Task TrackAsync_DelegatesToRepository_WithCorrectModeAndId(TrackingMode mode)
    {
        var sut = CreateSut();

        await sut.TrackAsync(42, mode);

        await _repository.Received(1).UpsertTrackedAsync(42, mode, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.TrackAsync(1, TrackingMode.Single, token);

        await _repository.Received(1).UpsertTrackedAsync(1, TrackingMode.Single, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  TrackTreeAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TrackTreeAsync_DelegatesToTrackAsync_WithTreeMode()
    {
        var sut = CreateSut();

        await sut.TrackTreeAsync(99);

        await _repository.Received(1).UpsertTrackedAsync(99, TrackingMode.Tree, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackTreeAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await sut.TrackTreeAsync(7, token);

        await _repository.Received(1).UpsertTrackedAsync(7, TrackingMode.Tree, token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  UntrackAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UntrackAsync_WhenTracked_RemovesAndReturnsTrue()
    {
        var sut = CreateSut();
        _repository.GetTrackedByWorkItemIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new TrackedItem(42, TrackingMode.Single, DateTimeOffset.UtcNow));

        var result = await sut.UntrackAsync(42);

        result.ShouldBeTrue();
        await _repository.Received(1).RemoveTrackedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UntrackAsync_WhenNotTracked_ReturnsFalseAndSkipsRemove()
    {
        var sut = CreateSut();
        _repository.GetTrackedByWorkItemIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((TrackedItem?)null);

        var result = await sut.UntrackAsync(42);

        result.ShouldBeFalse();
        await _repository.DidNotReceive().RemoveTrackedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UntrackAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        _repository.GetTrackedByWorkItemIdAsync(42, cts.Token)
            .Returns(new TrackedItem(42, TrackingMode.Single, DateTimeOffset.UtcNow));

        await sut.UntrackAsync(42, cts.Token);

        await _repository.Received(1).RemoveTrackedAsync(42, cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ExcludeAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExcludeAsync_DelegatesToRepository()
    {
        var sut = CreateSut();

        await sut.ExcludeAsync(55);

        await _repository.Received(1).AddExcludedAsync(55, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExcludeAsync_PassesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();

        await sut.ExcludeAsync(55, cts.Token);

        await _repository.Received(1).AddExcludedAsync(55, cts.Token);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetTrackedItemsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetTrackedItemsAsync_ReturnsAllTracked()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(1, TrackingMode.Single, DateTimeOffset.UtcNow),
            new(2, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var result = await sut.GetTrackedItemsAsync();

        result.ShouldBe(items);
    }

    [Fact]
    public async Task GetTrackedItemsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var result = await sut.GetTrackedItemsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetExcludedIdsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetExcludedIdsAsync_ReturnsWorkItemIds()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "irrelevant", DateTimeOffset.UtcNow),
            new(30, "done", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.GetExcludedIdsAsync();

        result.ShouldBe(new[] { 10, 20, 30 });
    }

    [Fact]
    public async Task GetExcludedIdsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.GetExcludedIdsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ListExclusionsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListExclusionsAsync_ReturnsAllExcludedItems()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "irrelevant", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.ListExclusionsAsync();

        result.ShouldBe(excluded);
    }

    [Fact]
    public async Task ListExclusionsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.ListExclusionsAsync();

        result.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  RemoveExclusionAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RemoveExclusionAsync_WhenExcluded_RemovesAndReturnsTrue()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem> { new(42, "noise", DateTimeOffset.UtcNow) };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.RemoveExclusionAsync(42);

        result.ShouldBeTrue();
        await _repository.Received(1).RemoveExcludedAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveExclusionAsync_WhenNotExcluded_ReturnsFalse()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.RemoveExclusionAsync(42);

        result.ShouldBeFalse();
        await _repository.DidNotReceive().RemoveExcludedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ClearExclusionsAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ClearExclusionsAsync_WithExclusions_ClearsAndReturnsCount()
    {
        var sut = CreateSut();
        var excluded = new List<ExcludedItem>
        {
            new(10, "noise", DateTimeOffset.UtcNow),
            new(20, "done", DateTimeOffset.UtcNow),
        };
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>()).Returns(excluded);

        var result = await sut.ClearExclusionsAsync();

        result.ShouldBe(2);
        await _repository.Received(1).ClearAllExcludedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearExclusionsAsync_WhenEmpty_ReturnsZeroAndSkipsClear()
    {
        var sut = CreateSut();
        _repository.GetAllExcludedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExcludedItem>());

        var result = await sut.ClearExclusionsAsync();

        result.ShouldBe(0);
        await _repository.DidNotReceive().ClearAllExcludedAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — helpers
    // ═══════════════════════════════════════════════════════════════

    private static SyncCoordinator CreateSyncCoordinator(
        IWorkItemRepository? workItemRepo = null,
        IAdoWorkItemService? adoService = null,
        IPendingChangeStore? pendingStore = null,
        int cacheStaleMinutes = 0)
    {
        workItemRepo ??= Substitute.For<IWorkItemRepository>();
        adoService ??= Substitute.For<IAdoWorkItemService>();
        pendingStore ??= Substitute.For<IPendingChangeStore>();

        // Default: no dirty items
        workItemRepo.GetDirtyItemsAsync().Returns(Array.Empty<WorkItem>());
        pendingStore.GetDirtyItemIdsAsync().Returns(Array.Empty<int>());

        var protectedWriter = new ProtectedCacheWriter(workItemRepo, pendingStore);
        return new SyncCoordinator(workItemRepo, adoService, protectedWriter, pendingStore, cacheStaleMinutes);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — no tree items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_NoTrackedItems_ReturnsZero()
    {
        var sut = CreateSut();
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var coordinator = CreateSyncCoordinator();
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncTrackedTreesAsync_OnlySingleModeItems_ReturnsZero()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(10, TrackingMode.Single, DateTimeOffset.UtcNow),
            new(20, TrackingMode.Single, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var adoService = Substitute.For<IAdoWorkItemService>();
        var coordinator = CreateSyncCoordinator(adoService: adoService);

        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0);
        await adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — tree items synced successfully
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_TreeItem_SyncsRootAndChildren()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(42, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        // SyncItemAsync path: item not in cache → fetch from ADO
        workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var fetched = new WorkItemBuilder(42, "Root").InState("Active").Build();
        adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fetched);

        // SyncChildrenAsync path: return children
        var children = new[] { new WorkItemBuilder(100, "Child").InState("Active").Build() };
        adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(children);

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0); // no items untracked
        await adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncTrackedTreesAsync_MultipleTreeItems_SyncsEach()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(10, TrackingMode.Tree, DateTimeOffset.UtcNow),
            new(20, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        workItemRepo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(10, "Root 10").InState("Active").Build());
        adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(20, "Root 20").InState("Active").Build());
        adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0);
        await adoService.Received(1).FetchAsync(10, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchAsync(20, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchChildrenAsync(10, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchChildrenAsync(20, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — deleted item auto-untracked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_DeletedItem_AutoUntracksAndSkipsChildren()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(42, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        // SyncItemAsync returns Failed with "not found"
        workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Work item 42 not found."));

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(1);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 42),
            Arg.Any<CancellationToken>());
        await adoService.DidNotReceive().FetchChildrenAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncTrackedTreesAsync_MultipleDeleted_BatchUntracks()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(10, TrackingMode.Tree, DateTimeOffset.UtcNow),
            new(20, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        workItemRepo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Work item 10 not found."));
        adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Work item 20 not found."));

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(2);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 2 && ids.Contains(10) && ids.Contains(20)),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — mixed: some deleted, some ok
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_MixedDeletedAndLive_UntracksOnlyDeleted()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(10, TrackingMode.Tree, DateTimeOffset.UtcNow),
            new(20, TrackingMode.Tree, DateTimeOffset.UtcNow),
            new(30, TrackingMode.Single, DateTimeOffset.UtcNow), // ignored
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        workItemRepo.GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        // Item 10 exists
        adoService.FetchAsync(10, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(10, "Root 10").InState("Active").Build());
        adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Item 20 deleted
        adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Work item 20 not found."));

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(1);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 20),
            Arg.Any<CancellationToken>());
        // Item 10 should still get children synced
        await adoService.Received(1).FetchChildrenAsync(10, Arg.Any<CancellationToken>());
        // Item 20 should NOT get children synced
        await adoService.DidNotReceive().FetchChildrenAsync(20, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — non-"not found" failure doesn't untrack
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_NonNotFoundFailure_DoesNotUntrack()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(42, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        // SyncItemAsync fails with a network error (not "not found")
        workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Children should still be attempted since item isn't deleted
        adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0);
        await _repository.DidNotReceive().RemoveTrackedBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  SyncTrackedTreesAsync — cancellation token propagation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SyncTrackedTreesAsync_PropagatesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        _repository.GetAllTrackedAsync(token).Returns(Array.Empty<TrackedItem>());

        var coordinator = CreateSyncCoordinator();
        await sut.SyncTrackedTreesAsync(coordinator, token);

        await _repository.Received(1).GetAllTrackedAsync(token);
    }
}
