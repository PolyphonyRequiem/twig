using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Cache-aware integration tests for <c>show --tree</c>.
/// Migrated from the former <c>TreeCommand_CacheAwareTests</c> to exercise
/// the same code paths through <see cref="ShowCommand"/> with <c>tree: true</c>.
/// </summary>
public sealed class ShowCommand_TreeCacheAwareTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ITelemetryClient _telemetryClient;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly string _tempDir;

    public ShowCommand_TreeCacheAwareTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _telemetryClient = Substitute.For<ITelemetryClient>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _linkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-tree-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db")));

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Helpers ────────────────────────────────────────────────────

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private CommandContext CreateCtx(RenderingPipelineFactory? pipelineFactory = null) =>
        new(pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }),
            new TwigConfiguration(),
            TelemetryClient: _telemetryClient);

    private TreeRenderingService CreateTreeService(CommandContext ctx) =>
        new(ctx, _contextStore, _workItemRepo, _activeItemResolver,
            _workingSetService, _syncCoordinatorFactory, _processTypeStore);

    private ShowCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null)
    {
        var ctx = CreateCtx(pipelineFactory);
        return new ShowCommand(ctx, _workItemRepo, _linkRepo,
            _syncCoordinatorFactory, _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            contextStore: _contextStore,
            activeItemResolver: _activeItemResolver,
            pendingChangeStore: _pendingChangeStore,
            workingSetService: _workingSetService,
            treeRenderingService: CreateTreeService(ctx));
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    private static async Task<string> CaptureStdout(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await action();
            return sw.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  --no-refresh flag: skips sync pass (tree path)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersTreeFromCacheOnly()
    {
        var item = new WorkItemBuilder(1, "Cached Tree Item").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true, noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Cached Tree Item");

        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(tree: true, noRefresh: true);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task NoRefresh_WithChildren_RendersTreeWithChildren()
    {
        var focus = new WorkItemBuilder(1, "Parent Item").Build();
        var child1 = new WorkItemBuilder(2, "Child One").WithParent(1).Build();
        var child2 = new WorkItemBuilder(3, "Child Two").WithParent(1).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true, noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Parent Item");
        output.ShouldContain("Child One");
        output.ShouldContain("Child Two");

        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Default path: two-pass rendering (tree path)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Default_TwoPassRendering_RendersCachedTreeThenSyncs()
    {
        var item = new WorkItemBuilder(1, "Two Pass Tree Item").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Two Pass Tree Item");
    }

    [Fact]
    public async Task Default_SyncFailure_FallsBackToDirectTreeRender()
    {
        var item = new WorkItemBuilder(1, "Fallback Tree Item").WithParent(99).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // First call to GetParentChainAsync throws (triggers fallback in two-pass path);
        // second call succeeds (fallback renders correctly).
        var parentChainCallCount = 0;
        _workItemRepo.GetParentChainAsync(99, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                parentChainCallCount++;
                if (parentChainCallCount == 1)
                    return Task.FromException<IReadOnlyList<WorkItem>>(new InvalidOperationException("Test error"));
                return Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>());
            });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Fallback Tree Item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Cache-age indicators in tree view
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoPass_StaleItem_ShowsCacheAgeIndicator()
    {
        var staleItem = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Parse("Task").Value,
            Title = "Stale Tree Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        };
        SetupActiveItem(staleItem);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("cached");
        output.ShouldContain("ago");
    }

    [Fact]
    public async Task TwoPass_FreshItem_NoCacheAgeIndicator()
    {
        var freshItem = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Parse("Task").Value,
            Title = "Fresh Tree Item",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };
        SetupActiveItem(freshItem);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task TwoPass_StaleChildren_ShowCacheAgeSuffix()
    {
        var focus = new WorkItemBuilder(1, "Focus Item").Build();
        var staleChild = new WorkItem
        {
            Id = 2,
            Type = WorkItemType.Parse("Task").Value,
            Title = "Stale Child",
            State = "New",
            ParentId = 1,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { staleChild });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Stale Child");
        output.ShouldContain("cached 2h ago");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Two-pass data change validation (tree path)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TwoPass_DataChangesAfterSync_RevisedViewUsesUpdatedData()
    {
        var preSync = new WorkItemBuilder(1, "Before Sync").WithParent(100).Build();
        var oldParent = new WorkItemBuilder(100, "Old Parent").AsEpic().Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var syncCompleted = false;

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (!syncCompleted)
                    return Task.FromResult<WorkItem?>(preSync);
                return Task.FromResult<WorkItem?>(
                    new WorkItemBuilder(1, "After Sync").WithParent(200).Build());
            });

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(oldParent);
        _workItemRepo.GetByIdAsync(200, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(200, "New Parent").AsEpic().Build());

        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { oldParent });
        _workItemRepo.GetParentChainAsync(200, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(200, "New Parent").AsEpic().Build() });
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { preSync });
        _workItemRepo.GetChildrenAsync(200, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(1, "After Sync").WithParent(200).Build() });

        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                syncCompleted = true;
                return Task.FromResult(
                    new WorkItemBuilder(callInfo.ArgAt<int>(0), "Fetched").Build());
            });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("After Sync");
        output.ShouldContain("New Parent");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Non-TTY sync-first: machine output with tree flag
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task NonTty_TreeFlag_RendersOutput(string format)
    {
        var item = new WorkItemBuilder(1, "Non-TTY Tree Item").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand(); // non-TTY pipeline (isOutputRedirected: true)
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, format, tree: true));

        output.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task NonTty_TreeFlag_NoRefresh_SkipsSync(string format)
    {
        var item = new WorkItemBuilder(1, "No Refresh Tree Machine").Build();
        SetupActiveItem(item);

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, format, tree: true, noRefresh: true));

        output.ShouldNotBeEmpty();
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Explicit ID with --tree and TTY (migrated from CacheAwareTests)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TreeTty_ExplicitId_RendersTreeInLiveView()
    {
        var item = new WorkItemBuilder(42, "TTY Tree By Id").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(42, "human", tree: true, noRefresh: true);

        result.ShouldBe(0);
        _testConsole.Output.ShouldContain("TTY Tree By Id");
    }

    [Fact]
    public async Task TreeTty_WithParentChain_RendersHierarchy()
    {
        var parent = new WorkItemBuilder(100, "Root Epic").AsEpic().Build();
        var focus = new WorkItemBuilder(1, "Focused Task").WithParent(100).Build();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { focus });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", tree: true, noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Root Epic");
        output.ShouldContain("Focused Task");
    }
}
