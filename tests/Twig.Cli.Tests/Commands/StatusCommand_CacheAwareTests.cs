using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class StatusCommand_CacheAwareTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly string _tempDir;
    private readonly TwigPaths _paths;

    public StatusCommand_CacheAwareTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        _config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private StatusCommand CreateCommandWithPipeline(RenderingPipelineFactory pipelineFactory, TextWriter? stderr = null) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, _config,
            _formatterFactory, _hintEngine, _activeItemResolver,
            _workingSetService, _syncCoordinatorFactory, _paths, pipelineFactory, stderr: stderr);

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    // ── --no-refresh flag: skips sync pass ──────────────────────────

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersFromCacheOnly()
    {
        var item = CreateWorkItem(1, "Cached Status Item");
        SetupActiveItem(item);

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Cached Status Item");

        // Verify ADO service was never called for sync (no fetch attempts)
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_WithPendingChanges_StillShowsPendingPanel()
    {
        var item = CreateWorkItem(1, "Dirty Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var pending = new PendingChangeRecord[]
        {
            new(1, "field", "System.Title", "Old", "New"),
            new(1, "note", null, null, "A note"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(pending);

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("local: 1 field change, 1 note");
    }

    [Fact]
    public async Task NoRefresh_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        using var errWriter = new StringWriter();
        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory(), stderr: errWriter);

        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("No active work item");
    }

    // ── Default path: two-pass rendering ────────────────────────────

    [Fact]
    public async Task Default_TwoPassRendering_RendersCachedThenSyncs()
    {
        var item = CreateWorkItem(1, "Two Pass Item");
        SetupActiveItem(item);

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        // noRefresh defaults to false — should use RenderWithSyncAsync
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Two Pass Item");
    }

    [Fact]
    public async Task Default_SyncFailure_FallsBackToStaticRender()
    {
        var item = CreateWorkItem(1, "Fallback Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Make ADO service throw to trigger the fallback path
        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Network error")));

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Fallback Item");
    }

    [Fact]
    public async Task NoRefresh_IsIndependentOfNoLive()
    {
        var item = CreateWorkItem(1, "Independent Flag Item");
        SetupActiveItem(item);

        // Both flags set: noLive + noRefresh
        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true, noRefresh: true);

        result.ShouldBe(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
