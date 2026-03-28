using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly SetCommand _cmd;

    public SetCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            workingSetService, formatterFactory, hintEngine);
    }

    [Fact]
    public async Task Set_NumericId_FromCache_SetsContext()
    {
        var item = CreateWorkItem(42, "Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_NumericId_FetchesFromAdo_WhenNotCached()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "Fetched Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_SingleMatch_SetsContext()
    {
        var item = CreateWorkItem(10, "Fix login bug");
        _workItemRepo.FindByPatternAsync("login", Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var result = await _cmd.ExecuteAsync("login");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
        // Pattern path never triggers eviction (fetchedFromAdo is always false)
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_MultiMatch_ReturnsError()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Fix login page"),
            CreateWorkItem(11, "Login timeout bug"),
        };
        _workItemRepo.FindByPatternAsync("login", Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await _cmd.ExecuteAsync("login");

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_NoMatch_ReturnsError()
    {
        _workItemRepo.FindByPatternAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync("nonexistent");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Set_EmptyInput_ReturnsUsageError()
    {
        var result = await _cmd.ExecuteAsync("");

        result.ShouldBe(2);
    }

    // ── WS-013: Eviction + WorkingSet tests ────────────────────────

    [Fact]
    public async Task Set_CacheMiss_TriggersEviction()
    {
        // Arrange: cache miss → FetchedFromAdo path
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // EvictExceptAsync called on cache miss (FR-012: FetchedFromAdo triggers eviction)
        await _workItemRepo.Received(1).EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_CacheHit_SkipsEviction()
    {
        // Arrange: cache hit → Found path
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // EvictExceptAsync NOT called on cache hit (FR-012)
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_CacheMiss_EvictionKeepsWorkingSetIds()
    {
        // Arrange: cache miss with active item and children in working set
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var child = CreateWorkItem(100, "Child");
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        IReadOnlySet<int>? capturedKeepIds = null;
        await _workItemRepo.EvictExceptAsync(
            Arg.Do<IReadOnlySet<int>>(ids => capturedKeepIds = ids),
            Arg.Any<CancellationToken>());

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        capturedKeepIds.ShouldNotBeNull();
        capturedKeepIds.ShouldContain(42);  // active item
        capturedKeepIds.ShouldContain(100); // child
    }

    [Fact]
    public async Task Set_CacheMiss_DirtyItemsSurviveEviction()
    {
        // Arrange: cache miss with a dirty item
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        var dirtyItem = CreateWorkItem(99, "Dirty");
        dirtyItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { dirtyItem });

        IReadOnlySet<int>? capturedKeepIds = null;
        await _workItemRepo.EvictExceptAsync(
            Arg.Do<IReadOnlySet<int>>(ids => capturedKeepIds = ids),
            Arg.Any<CancellationToken>());

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        capturedKeepIds.ShouldNotBeNull();
        // Dirty item ID is in the keep set (dirty items survive eviction)
        capturedKeepIds.ShouldContain(99);
    }

    [Fact]
    public async Task Set_CacheHit_StillCallsSyncWorkingSet()
    {
        // Arrange: cache hit → Found path, but working set sync still fires
        // Item is explicitly stale (LastSyncedAt = null) so SyncCoordinator will attempt a refresh
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // SyncWorkingSetAsync processes stale items (LastSyncedAt is null → stale).
        // The observable effect is that FetchAsync is called for the stale active item.
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        // But EvictExceptAsync NOT called (FR-012: cache hit skips eviction)
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    // ── Navigation History (Epic 2) ───────────────────────────────

    [Fact]
    public async Task Set_RecordsNavigationHistory_WhenHistoryStoreProvided()
    {
        var item = CreateWorkItem(42, "Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var historyStore = Substitute.For<INavigationHistoryStore>();

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            workingSetService, formatterFactory, hintEngine, historyStore: historyStore);

        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await historyStore.Received(1).RecordVisitAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_NullHistoryStore_DoesNotThrow()
    {
        var item = CreateWorkItem(42, "Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // _cmd is created without historyStore (null) — should succeed
        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
