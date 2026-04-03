using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly WorkingSetService _workingSetService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly SetCommand _cmd;

    public SetCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            _workingSetService, _formatterFactory, _hintEngine);
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

    [Fact]
    public async Task Set_CacheHit_SyncsTargetAndParentChain()
    {
        // Arrange: cache hit → Found path, targeted sync fires for item + parents
        // Item is explicitly stale (LastSyncedAt = null) so SyncCoordinator will attempt a refresh
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // SyncItemSetAsync syncs ONLY the target item (exactly 1 ADO fetch — no working set IDs)
        await _adoService.Received(1).FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_WithParentChain_SyncsParentIds()
    {
        // Arrange: item 42 has parent 100, both stale (null LastSyncedAt)
        var parent = CreateWorkItem(100, "Parent");
        var item = CreateWorkItem(42, "Child Item").WithParentId(100);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // SyncItemSetAsync scope = [42, 100]: exactly 2 ADO fetches (both stale — null LastSyncedAt)
        await _adoService.Received(2).FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        await _adoService.Received().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_CacheMiss_WithParentChain_SyncsParentIds()
    {
        // Cache miss with parent chain — verify SyncItemSetAsync receives parent IDs
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var parent = CreateWorkItem(100, "Parent");
        var item = CreateWorkItem(42, "Child Item").WithParentId(100);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);

        // Hydration path: GetParentChainAsync(100) returns the parent
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        // ComputeAsync path: GetParentChainAsync(42) for working set computation
        _workItemRepo.GetParentChainAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // Eviction fires on cache miss (FR-012)
        await _workItemRepo.Received(1).EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
        // SyncItemSetAsync syncs both target and parent (both stale — null LastSyncedAt)
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        await _adoService.Received().FetchAsync(100, Arg.Any<CancellationToken>());
    }

    // ── Navigation History (Epic 2) ───────────────────────────────

    [Fact]
    public async Task Set_RecordsNavigationHistory_WhenHistoryStoreProvided()
    {
        var item = CreateWorkItem(42, "Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var historyStore = Substitute.For<INavigationHistoryStore>();

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            _workingSetService, _formatterFactory, _hintEngine, historyStore: historyStore);

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

    // ── Process Configuration (T1403) ───────────────────────────────

    [Fact]
    public async Task Set_WithProcessConfig_UsesStateEntriesToComputeChildProgress()
    {
        // Arrange: parent with children in "Done" state — Basic process maps "Done" → Completed
        var parent = CreateWorkItem(100, "Parent Epic", "Epic");
        var child1 = CreateWorkItem(101, "Child 1", "Issue", "Done");
        var child2 = CreateWorkItem(102, "Child 2", "Issue", "Done");
        var child3 = CreateWorkItem(103, "Child 3", "Issue", "Doing");

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2, child3 });

        var processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            _workingSetService, _formatterFactory, _hintEngine,
            processConfigProvider: processConfigProvider);

        // Act
        var result = await cmd.ExecuteAsync("100");

        // Assert: command succeeds, process config was queried
        result.ShouldBe(0);
        processConfigProvider.Received().GetConfiguration();
    }

    [Fact]
    public async Task Set_NullProcessConfigProvider_FallsBackGracefully()
    {
        // Arrange: no process config — should fall back to hardcoded heuristics without crashing
        var parent = CreateWorkItem(100, "Parent Epic", "Epic");
        var child = CreateWorkItem(101, "Child", "Task", "Closed");

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child });

        // _cmd is created without processConfigProvider (null) — should not throw
        var result = await _cmd.ExecuteAsync("100");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Set_WithProcessConfig_ScrumDoneState_QueriesProcessConfig()
    {
        // Arrange: Scrum process template where PBI "Done" → Completed via state entries
        var parent = CreateWorkItem(200, "Sprint backlog", "Feature");
        var pbi1 = CreateWorkItem(201, "PBI 1", "Product Backlog Item", "Done");
        var pbi2 = CreateWorkItem(202, "PBI 2", "Product Backlog Item", "New");

        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(200, Arg.Any<CancellationToken>())
            .Returns(new[] { pbi1, pbi2 });

        var processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Scrum());

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            _workingSetService, _formatterFactory, _hintEngine,
            processConfigProvider: processConfigProvider);

        // Act
        var result = await cmd.ExecuteAsync("200");

        // Assert: command succeeds — Scrum "Done" recognized via state entries
        result.ShouldBe(0);
        processConfigProvider.Received().GetConfiguration();
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return CreateWorkItem(id, title, "Task");
    }

    private static WorkItem CreateWorkItem(int id, string title, string typeName, string state = "New")
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(typeName).Value,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
