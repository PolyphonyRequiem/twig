using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinatorPair _syncCoordinatorPair;
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
        _syncCoordinatorPair = new SyncCoordinatorPair(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorPair,
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

        var cmd = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorPair,
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
    public async Task Set_WithProcessConfig_CallsProcessConfigProvider()
    {
        var parent = CreateWorkItem(100, "Parent Epic", "Epic");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(101, "Child 1", "Issue", "Done"),
            CreateWorkItem(102, "Child 2", "Issue", "Doing"),
        });
        var provider = CreateProvider(ProcessConfigBuilder.Basic());

        var result = await CreateCommand(provider).ExecuteAsync("100");

        result.ShouldBe(0);
        provider.Received().GetConfiguration();
    }

    [Fact]
    public async Task Set_NullProcessConfigProvider_FallsBackGracefully()
    {
        var parent = CreateWorkItem(100, "Parent Epic", "Epic");
        ArrangeParentWithChildren(parent, new[] { CreateWorkItem(101, "Child", "Task", "Closed") });

        var result = await _cmd.ExecuteAsync("100");

        result.ShouldBe(0);
    }

    // ── Progress Output (T1403) ──────────────────────────────────────

    [Fact]
    public async Task BasicConfig_TwoDoneOneDoing_OutputContains2Of3()
    {
        var parent = CreateWorkItem(100, "Parent Epic", "Epic");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(101, "Child 1", "Issue", "Done"),
            CreateWorkItem(102, "Child 2", "Issue", "Done"),
            CreateWorkItem(103, "Child 3", "Issue", "Doing"),
        });

        var output = await ExecuteCapturingOutput(CreateCommand(CreateProvider(ProcessConfigBuilder.Basic())), "100");

        output.ShouldContain("2/3");
    }

    [Fact]
    public async Task AgileConfig_FourClosedOneActive_OutputContains4Of5()
    {
        // H1 bug scenario: Agile "Closed" was previously unrecognized by the heuristic
        var parent = CreateWorkItem(200, "User Story", "User Story");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(201, "Task 1", "Task", "Closed"),
            CreateWorkItem(202, "Task 2", "Task", "Closed"),
            CreateWorkItem(203, "Task 3", "Task", "Closed"),
            CreateWorkItem(204, "Task 4", "Task", "Closed"),
            CreateWorkItem(205, "Task 5", "Task", "Active"),
        });

        var output = await ExecuteCapturingOutput(CreateCommand(CreateProvider(ProcessConfigBuilder.Agile())), "200");

        output.ShouldContain("4/5");
    }

    [Fact]
    public async Task ScrumConfig_OneDoneOneNew_OutputContains1Of2()
    {
        var parent = CreateWorkItem(300, "Sprint backlog", "Feature");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(301, "PBI 1", "Product Backlog Item", "Done"),
            CreateWorkItem(302, "PBI 2", "Product Backlog Item", "New"),
        });

        var output = await ExecuteCapturingOutput(CreateCommand(CreateProvider(ProcessConfigBuilder.Scrum())), "300");

        output.ShouldContain("1/2");
    }

    [Fact]
    public async Task NullProvider_ClosedTask_FallsBackToHeuristic()
    {
        var parent = CreateWorkItem(400, "Parent", "Epic");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(401, "Task 1", "Task", "Closed"),
            CreateWorkItem(402, "Task 2", "Task", "Active"),
        });

        var output = await ExecuteCapturingOutput(CreateCommand(processConfigProvider: null), "400");

        output.ShouldContain("1/2");
    }

    [Fact]
    public async Task CustomState_OnlyRecognizedViaProcessConfig()
    {
        var config = new ProcessConfigBuilder()
            .AddType("Task", ProcessConfigBuilder.S(
                ("Backlog", StateCategory.Proposed),
                ("Working", StateCategory.InProgress),
                ("Shipped", StateCategory.Completed)))
            .Build();
        var parent = CreateWorkItem(500, "Parent", "Epic");
        ArrangeParentWithChildren(parent, new[]
        {
            CreateWorkItem(501, "Task 1", "Task", "Shipped"),
            CreateWorkItem(502, "Task 2", "Task", "Working"),
        });

        var output = await ExecuteCapturingOutput(CreateCommand(CreateProvider(config)), "500");

        output.ShouldContain("1/2");
    }

    [Fact]
    public async Task Set_CacheMiss_EvictionIncludesExtendedParents()
    {
        // Arrange: cache miss → fetchedFromAdo = true
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var grandparent = CreateWorkItem(200, "Grandparent");
        var parent = CreateWorkItem(100, "Parent").WithParentId(200);
        var item = CreateWorkItem(42, "Child").WithParentId(100);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(200, Arg.Any<CancellationToken>()).Returns(grandparent);
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Parent chain hydration in SetCommand (direct parent only)
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });

        // After extension, working set computation sees full parent chain
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetParentChainAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { parent, grandparent });

        var result = await CreateCommandWithContextChange().ExecuteAsync("42");

        result.ShouldBe(0);
        // Eviction keep set should include grandparent 200 (from expanded working set)
        await _workItemRepo.Received().EvictExceptAsync(
            Arg.Is<IReadOnlySet<int>>(ids => ids.Contains(200)),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private SetCommand CreateCommandWithContextChange()
    {
        var pendingStore = Substitute.For<IPendingChangeStore>();
        var protectedWriter = new ProtectedCacheWriter(_workItemRepo, pendingStore);
        var factory = new SyncCoordinatorPair(_workItemRepo, _adoService, protectedWriter, pendingStore, null, 30, 30);
        var contextChangeService = new ContextChangeService(
            _workItemRepo, _adoService, factory.ReadWrite, protectedWriter);
        return new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, factory,
            _workingSetService, _formatterFactory, _hintEngine,
            contextChangeService: contextChangeService);
    }

    private void ArrangeParentWithChildren(WorkItem parent, WorkItem[] children)
    {
        _workItemRepo.GetByIdAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(parent.Id, Arg.Any<CancellationToken>()).Returns(children);
    }

    private static IProcessConfigurationProvider CreateProvider(ProcessConfiguration config)
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);
        return provider;
    }

    private SetCommand CreateCommand(IProcessConfigurationProvider? processConfigProvider)
    {
        return new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinatorPair,
            _workingSetService, _formatterFactory, _hintEngine,
            processConfigProvider: processConfigProvider);
    }

    private static async Task<string> ExecuteCapturingOutput(SetCommand cmd, string idOrPattern)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync(idOrPattern);
            result.ShouldBe(0);
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static WorkItem CreateWorkItem(int id, string title, string typeName = "Task", string state = "New")
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
