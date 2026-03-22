using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StatusCommandAsyncTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;

    public StatusCommandAsyncTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = true });
    }

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a TTY environment
    /// (isOutputRedirected returns false) so the async rendering path is selected.
    /// </summary>
    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a redirected/piped
    /// environment (isOutputRedirected returns true) so the sync fallback is selected.
    /// </summary>
    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private StatusCommand CreateCommand(RenderingPipelineFactory pipelineFactory, TextWriter? stderr = null) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, _config,
            _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinator,
            pipelineFactory, stderr: stderr);

    // ── Async rendering path tests ──────────────────────────────────

    [Fact]
    public async Task AsyncPath_RendersDashboardWithWorkItemPanel()
    {
        var item = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Active Item");
        output.ShouldContain("New");
    }

    [Fact]
    public async Task AsyncPath_DirtyItem_ShowsDirtyMarker()
    {
        var item = CreateWorkItem(1, "Dirty Item");
        item.SetDirty();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Dirty Item");
        output.ShouldContain("•");
    }

    [Fact]
    public async Task AsyncPath_WithPendingChanges_ShowsPendingPanel()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        var pending = new PendingChangeRecord[]
        {
            new(1, "field", "System.Title", "Old", "New"),
            new(1, "note", null, null, "A note"),
            new(1, "note", null, null, "Another note"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(pending);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Pending Changes");
        output.ShouldContain("1"); // 1 field change
        output.ShouldContain("2"); // 2 notes
    }

    [Fact]
    public async Task AsyncPath_NoPendingChanges_NoPendingPanel()
    {
        var item = CreateWorkItem(1, "Clean Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Clean Item");
        output.ShouldNotContain("Pending Changes");
    }

    [Fact]
    public async Task AsyncPath_WithStaleSeeds_ShowsHint()
    {
        var item = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var staleSeed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Stale Seed",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { staleSeed });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("stale");
    }

    [Fact]
    public async Task AsyncPath_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        using var errWriter = new StringWriter();
        var cmd = CreateCommand(CreateTtyPipelineFactory(), stderr: errWriter);

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("No active work item");
    }

    [Fact]
    public async Task AsyncPath_ItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        using var errWriter = new StringWriter();
        var cmd = CreateCommand(CreateTtyPipelineFactory(), stderr: errWriter);

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("#42");
        errWriter.ToString().ShouldContain("not found in cache");
    }

    // ── Sync fallback tests ─────────────────────────────────────────

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        var item = CreateWorkItem(1, "Sync Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncFallback_JsonFormat_UsesSyncPath()
    {
        var item = CreateWorkItem(1, "JSON Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncFallback_NoLiveFlag_UsesSyncPath()
    {
        var item = CreateWorkItem(1, "NoLive Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true);

        result.ShouldBe(0);
    }

    // ── SpectreRenderer unit tests ──────────────────────────────────

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_ShowsItemDetails()
    {
        var item = CreateWorkItem(1, "Dashboard Item");

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Dashboard Item");
        output.ShouldContain("New");
        output.ShouldContain("(unassigned)");
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_DirtyItem_ShowsMarker()
    {
        var item = CreateWorkItem(1, "Dirty Dashboard");
        item.SetDirty();

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Dirty Dashboard");
        output.ShouldContain("•");
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_WithPendingChanges_ShowsPanel()
    {
        var item = CreateWorkItem(1, "Pending Item");
        var pending = new PendingChangeRecord[]
        {
            new(1, "field", "System.Title", "Old", "New"),
            new(1, "field", "System.State", "New", "Active"),
            new(1, "note", null, null, "A note"),
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(pending),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Pending Changes");
        output.ShouldContain("2"); // 2 field changes
        output.ShouldContain("1"); // 1 note
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_NullItem_NoOutput()
    {
        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(null),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        _testConsole.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_ShowsAssignedTo()
    {
        var item = new WorkItem
        {
            Id = 5,
            Type = WorkItemType.Task,
            Title = "Assigned Item",
            State = "Active",
            AssignedTo = "Jane Doe",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project\\Team A").Value,
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Jane Doe");
        output.ShouldContain("Team A");
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
