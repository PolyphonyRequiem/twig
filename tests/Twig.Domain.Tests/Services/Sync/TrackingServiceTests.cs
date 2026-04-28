using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

public sealed class TrackingServiceTests
{
    private readonly ITrackingRepository _repository = Substitute.For<ITrackingRepository>();
    private readonly IWorkItemRepository _workItemRepository = Substitute.For<IWorkItemRepository>();
    private readonly IProcessTypeStore _processTypeStore = Substitute.For<IProcessTypeStore>();

    private TrackingService CreateSut() => new(_repository, _workItemRepository, _processTypeStore);

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

        // Default: FetchWithLinksAsync returns empty links (for SyncRootLinksAsync)
        adoService.FetchWithLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var id = callInfo.ArgAt<int>(0);
                var item = new WorkItemBuilder(id, $"Item {id}").InState("Active").Build();
                return (item, (IReadOnlyList<WorkItemLink>)Array.Empty<WorkItemLink>());
            });

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
        // Root links synced via FetchWithLinksAsync
        await adoService.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncTrackedTreesAsync_TreeItem_SyncsRootLinkTargets()
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

        adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Link targets are stale (not in cache) → fetched by SyncItemSetAsync
        workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(100, "Target A").InState("Active").Build());
        adoService.FetchAsync(200, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(200, "Target B").InState("Active").Build());

        // Create coordinator first (sets up default empty-links stub)
        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);

        // Override with specific links AFTER helper (last matching stub wins)
        var links = new List<WorkItemLink>
        {
            new(42, 100, "Related"),
            new(42, 200, "Predecessor"),
        };
        adoService.FetchWithLinksAsync(42, Arg.Any<CancellationToken>())
            .Returns((fetched, (IReadOnlyList<WorkItemLink>)links));

        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(0);
        await adoService.Received(1).FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchAsync(100, Arg.Any<CancellationToken>());
        await adoService.Received(1).FetchAsync(200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncTrackedTreesAsync_DeletedItem_SkipsRootLinks()
    {
        var sut = CreateSut();
        var items = new List<TrackedItem>
        {
            new(42, TrackingMode.Tree, DateTimeOffset.UtcNow),
        };
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>()).Returns(items);

        var workItemRepo = Substitute.For<IWorkItemRepository>();
        var adoService = Substitute.For<IAdoWorkItemService>();

        workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Work item 42 not found."));

        var coordinator = CreateSyncCoordinator(workItemRepo, adoService);
        var result = await sut.SyncTrackedTreesAsync(coordinator);

        result.ShouldBe(1);
        await adoService.DidNotReceive().FetchWithLinksAsync(42, Arg.Any<CancellationToken>());
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

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — helpers
    // ═══════════════════════════════════════════════════════════════

    private static IterationPath TestIteration(string path = @"Project\Sprint 1") =>
        IterationPath.Parse(path).Value;

    private static TrackedItem MakeTracked(int id, TrackingMode mode = TrackingMode.Single) =>
        new(id, mode, DateTimeOffset.UtcNow);

    private void SetupTrackedItems(params TrackedItem[] items)
    {
        _repository.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(items.ToList().AsReadOnly());
    }

    private void SetupWorkItems(params WorkItem[] items)
    {
        _workItemRepository.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(items.ToList().AsReadOnly());
    }

    private void SetupProcessType(string typeName, params StateEntry[] states)
    {
        var record = new ProcessTypeRecord { TypeName = typeName, States = states };
        _processTypeStore.GetByNameAsync(typeName, Arg.Any<CancellationToken>())
            .Returns(record);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — None policy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_NonePolicy_ReturnsZero_NeverQueriesRepository()
    {
        var sut = CreateSut();

        var result = await sut.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.None, TestIteration());

        result.ShouldBe(0);
        await _repository.DidNotReceive().GetAllTrackedAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — empty tracked list
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_NoTrackedItems_ReturnsZero()
    {
        var sut = CreateSut();
        SetupTrackedItems();

        var result = await sut.ApplyCleanupPolicyAsync(TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(0);
        await _repository.DidNotReceive().RemoveTrackedBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — OnComplete removes completed items
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnComplete_RemovesCompletedItems()
    {
        var sut = CreateSut();

        SetupTrackedItems(MakeTracked(1), MakeTracked(2), MakeTracked(3));
        SetupWorkItems(
            new WorkItemBuilder(1, "Done Task").AsTask().InState("Closed").Build(),
            new WorkItemBuilder(2, "Active Task").AsTask().InState("Active").Build(),
            new WorkItemBuilder(3, "Also Done").AsTask().InState("Done").Build());

        SetupProcessType("Task",
            new StateEntry("Active", StateCategory.InProgress, null),
            new StateEntry("Closed", StateCategory.Completed, null),
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(2);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 2 && ids.Contains(1) && ids.Contains(3)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnComplete_KeepsNonCompletedItems()
    {
        var sut = CreateSut();

        SetupTrackedItems(MakeTracked(10));
        SetupWorkItems(
            new WorkItemBuilder(10, "In Progress").AsTask().InState("Active").Build());

        SetupProcessType("Task",
            new StateEntry("Active", StateCategory.InProgress, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(0);
        await _repository.DidNotReceive().RemoveTrackedBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — OnCompleteAndPast requires both conditions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnCompleteAndPast_RemovesCompletedInPastIteration()
    {
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 2");

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "Old Done").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 1").Build());

        SetupProcessType("Task",
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        result.ShouldBe(1);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnCompleteAndPast_KeepsCompletedInCurrentIteration()
    {
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 2");

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "Current Done").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 2").Build());

        SetupProcessType("Task",
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        result.ShouldBe(0);
        await _repository.DidNotReceive().RemoveTrackedBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnCompleteAndPast_KeepsActiveInPastIteration()
    {
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 2");

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "Old Active").AsTask().InState("Active")
                .WithIterationPath(@"Project\Sprint 1").Build());

        SetupProcessType("Task",
            new StateEntry("Active", StateCategory.InProgress, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        result.ShouldBe(0);
        await _repository.DidNotReceive().RemoveTrackedBatchAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — process-agnostic (fallback resolution)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_FallsBackToHeuristic_WhenNoProcessType()
    {
        var sut = CreateSut();

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "Closed Item").AsTask().InState("Closed").Build());

        // No process type configured — StateCategoryResolver falls back to heuristic
        _processTypeStore.GetByNameAsync("Task", Arg.Any<CancellationToken>())
            .Returns((ProcessTypeRecord?)null);

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(1);
    }

    [Fact]
    public async Task ApplyCleanupPolicyAsync_ProcessAgnostic_WorksWithScrum()
    {
        var sut = CreateSut();

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "PBI").AsProductBacklogItem().InState("Done").Build());

        SetupProcessType("Product Backlog Item",
            new StateEntry("New", StateCategory.Proposed, null),
            new StateEntry("Approved", StateCategory.InProgress, null),
            new StateEntry("Committed", StateCategory.InProgress, null),
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — skips items not in cache
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_SkipsItemsNotInCache()
    {
        var sut = CreateSut();

        SetupTrackedItems(MakeTracked(1), MakeTracked(99));
        // Only work item 1 is in cache; 99 is not
        SetupWorkItems(
            new WorkItemBuilder(1, "Done").AsTask().InState("Done").Build());

        SetupProcessType("Task",
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration());

        result.ShouldBe(1);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids[0] == 1),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — mixed batch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnCompleteAndPast_MixedBatch_OnlyRemovesQualifying()
    {
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 3");

        SetupTrackedItems(MakeTracked(1), MakeTracked(2), MakeTracked(3), MakeTracked(4));
        SetupWorkItems(
            new WorkItemBuilder(1, "Done past").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 1").Build(),
            new WorkItemBuilder(2, "Done current").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 3").Build(),
            new WorkItemBuilder(3, "Active past").AsTask().InState("Active")
                .WithIterationPath(@"Project\Sprint 2").Build(),
            new WorkItemBuilder(4, "Done also past").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 2").Build());

        SetupProcessType("Task",
            new StateEntry("Active", StateCategory.InProgress, null),
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        // Only items 1 and 4 are completed AND in past iterations
        result.ShouldBe(2);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 2 && ids.Contains(1) && ids.Contains(4)),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — future iteration behavior
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_OnCompleteAndPast_RemovesCompletedInFutureIteration_BehaviorDocumented()
    {
        // IterationPath has no total ordering, so != is used instead of <.
        // This means a completed item in a future sprint is also removed.
        // This test documents that intentional behavior (see inline comment in TrackingService).
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 2");

        SetupTrackedItems(MakeTracked(1));
        SetupWorkItems(
            new WorkItemBuilder(1, "Future Done").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 5").Build());

        SetupProcessType("Task",
            new StateEntry("Done", StateCategory.Completed, null));

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        // Completed item in a future iteration is still removed (!= not <)
        result.ShouldBe(1);
        await _repository.Received(1).RemoveTrackedBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Count == 1 && ids.Contains(1)),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — process type cache (N+1 elimination)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_CachesProcessTypeLookups_AvoidsDuplicateCalls()
    {
        var sut = CreateSut();
        var currentIteration = TestIteration(@"Project\Sprint 2");

        // Three items, all same type — should only call GetByNameAsync once
        SetupTrackedItems(MakeTracked(1), MakeTracked(2), MakeTracked(3));
        SetupWorkItems(
            new WorkItemBuilder(1, "Task A").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 1").Build(),
            new WorkItemBuilder(2, "Task B").AsTask().InState("Active")
                .WithIterationPath(@"Project\Sprint 2").Build(),
            new WorkItemBuilder(3, "Task C").AsTask().InState("Done")
                .WithIterationPath(@"Project\Sprint 1").Build());

        SetupProcessType("Task",
            new StateEntry("Active", StateCategory.InProgress, null),
            new StateEntry("Done", StateCategory.Completed, null));

        await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnCompleteAndPast, currentIteration);

        // GetByNameAsync should be called exactly once for "Task", not 3 times
        await _processTypeStore.Received(1).GetByNameAsync("Task", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ApplyCleanupPolicyAsync — cancellation token propagation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApplyCleanupPolicyAsync_PropagatesCancellationToken()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        SetupTrackedItems();

        var result = await sut.ApplyCleanupPolicyAsync(
            TrackingCleanupPolicy.OnComplete, TestIteration(), token);

        result.ShouldBe(0);
        await _repository.Received(1).GetAllTrackedAsync(token);
    }
}
