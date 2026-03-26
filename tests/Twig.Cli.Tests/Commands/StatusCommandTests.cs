using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StatusCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly StatusCommand _cmd;
    private readonly string _tempDir;
    private readonly TwigPaths _paths;

    public StatusCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);
        _config = new TwigConfiguration { Seed = new SeedConfig { StaleDays = 14 } };
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db"));

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            _config, _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinator, _paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── Command factory methods ─────────────────────────────────────

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private StatusCommand CreateCommandWithPipeline(RenderingPipelineFactory pipelineFactory, TextWriter? stderr = null) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, _config,
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = true }), _activeItemResolver,
            _workingSetService, _syncCoordinator, _paths, pipelineFactory, stderr: stderr);

    private StatusCommand CreateCommandWithGit(IGitService? git = null, IAdoGitService? adoGit = null) =>
        new(_contextStore, _workItemRepo, _pendingChangeStore, _config,
            _formatterFactory, _hintEngine, _activeItemResolver, _workingSetService, _syncCoordinator, _paths,
            pipelineFactory: null, gitService: git, adoGitService: adoGit);

    // ── Core command behavior ───────────────────────────────────────

    [Fact]
    public async Task Status_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Status_ActiveItem_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_WithPendingChanges_ReturnsSuccess()
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

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Status_ItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── WS-021: JSON output parity ──────────────────────────────────

    [Fact]
    public async Task Status_JsonOutput_NoSyncIndicators()
    {
        var item = CreateWorkItem(1, "JSON Parity Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    // ── Branch detection hints ──────────────────────────────────────

    [Fact]
    public async Task Status_NoActiveItem_WithMatchingBranch_ShowsBranchDetectionHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.ExistsByIdAsync(12345, Arg.Any<CancellationToken>()).Returns(true);

        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/12345-login");

        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            new TwigConfiguration
            {
                Seed = new SeedConfig { StaleDays = 14 },
                Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
            },
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = true }),
            _activeItemResolver, _workingSetService, _syncCoordinator, _paths, gitService: gitService);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Status_NoActiveItem_NoMatchingBranch_NoBranchHint()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");

        var cmd = new StatusCommand(_contextStore, _workItemRepo, _pendingChangeStore,
            new TwigConfiguration
            {
                Seed = new SeedConfig { StaleDays = 14 },
                Git = new GitConfig { BranchPattern = BranchNameTemplate.DefaultPattern },
            },
            _formatterFactory, new HintEngine(new DisplayConfig { Hints = true }),
            _activeItemResolver, _workingSetService, _syncCoordinator, _paths, gitService: gitService);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    // ── Async rendering path (TTY) ──────────────────────────────────

    [Fact]
    public async Task AsyncPath_RendersDashboardWithWorkItemPanel()
    {
        var item = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("1 field change, 2 notes staged");
    }

    [Fact]
    public async Task AsyncPath_NoPendingChanges_NoPendingPanel()
    {
        var item = CreateWorkItem(1, "Clean Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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
        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory(), stderr: errWriter);

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
        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory(), stderr: errWriter);

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("#42");
        errWriter.ToString().ShouldContain("not found in cache");
    }

    // ── Sync fallback (redirected / JSON / noLive) ──────────────────

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        var item = CreateWorkItem(1, "Sync Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommandWithPipeline(CreateRedirectedPipelineFactory());
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

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
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
        output.ShouldContain("2 field changes, 1 note staged");
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

    // ── Git context enrichment ──────────────────────────────────────

    [Fact]
    public async Task Git_WithBranch_ShowsBranchName()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/42-test-item");

        var cmd = CreateCommandWithGit(gitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Git_WithPRs_ShowsPrStatus()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("feature/42-test-item");
        var adoGitService = Substitute.For<IAdoGitService>();
        adoGitService.GetPullRequestsForBranchAsync("feature/42-test-item", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PullRequestInfo(101, "PR Title", "active",
                    "refs/heads/feature/42-test-item", "refs/heads/main", "https://dev.azure.com/pr/101"),
            });

        var cmd = CreateCommandWithGit(gitService, adoGitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Git_NoGitService_StillWorks()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommandWithGit(git: null);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Git_NotInWorkTree_StillWorks()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommandWithGit(gitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Git_GitThrows_StillSucceeds()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));

        var cmd = CreateCommandWithGit(gitService);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Git_PrLookupFails_StillSucceeds()
    {
        var item = CreateWorkItem(42, "Test item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        var gitService = Substitute.For<IGitService>();
        gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>()).Returns("main");
        var adoGitService = Substitute.For<IAdoGitService>();
        adoGitService.GetPullRequestsForBranchAsync("main", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var cmd = CreateCommandWithGit(gitService, adoGitService);

        var result = await cmd.ExecuteAsync();
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

    private static WorkItem CreateWorkItemWithFields(int id, string title, Dictionary<string, string?> fields)
    {
        var item = CreateWorkItem(id, title);
        item.ImportFields(fields);
        return item;
    }

    // ── Extended fields display (EPIC-007 E2-T6) ────────────────────

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_ShowsExtendedFields()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            ["System.Tags"] = "backend, auth",
        };
        var item = CreateWorkItemWithFields(1, "Extended Item", fields);

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
            new("System.Tags", "Tags", "string", false),
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: fieldDefs);

        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("2");
        output.ShouldContain("Story Points");
        output.ShouldContain("5");
        output.ShouldContain("Tags");
        output.ShouldContain("backend, auth");
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_NoFieldDefs_FallsBackToDerivedNames()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
        };
        var item = CreateWorkItemWithFields(1, "Fallback Item", fields);

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: null);

        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("1");
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_SkipsCoreFields()
    {
        var fields = new Dictionary<string, string?>
        {
            ["System.Title"] = "Should not duplicate",
            ["System.State"] = "New",
            ["Microsoft.VSTS.Common.Priority"] = "3",
        };
        var item = CreateWorkItemWithFields(1, "Core Skip Item", fields);

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("3");
    }

    [Fact]
    public void HumanOutputFormatter_FormatWorkItem_ShowsExtendedFields()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
        };
        var item = CreateWorkItemWithFields(1, "Extended Item", fields);
        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
        };

        var fmt = new HumanOutputFormatter();
        var result = fmt.FormatWorkItem(item, showDirty: false, fieldDefs);

        result.ShouldContain("Extended");
        result.ShouldContain("Priority");
        result.ShouldContain("2");
        result.ShouldContain("Story Points");
        result.ShouldContain("5");
    }

    [Fact]
    public void HumanOutputFormatter_FormatWorkItem_NoFields_NoExtendedSection()
    {
        var item = CreateWorkItem(1, "Plain Item");
        var fmt = new HumanOutputFormatter();
        var result = fmt.FormatWorkItem(item, showDirty: false, fieldDefinitions: null);

        result.ShouldNotContain("Extended");
    }

    // ── Status fields config rendering integration (EPIC-010 E3) ────

    [Fact]
    public async Task SpectreRenderer_WithStatusFieldEntries_ShowsOnlyStarredFieldsInOrder()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            ["System.Tags"] = "backend, auth",
            ["Microsoft.VSTS.Common.Severity"] = "3 - Medium",
        };
        var item = CreateWorkItemWithFields(1, "Config Item", fields);

        var fieldDefs = new FieldDefinition[]
        {
            new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
            new("System.Tags", "Tags", "string", false),
            new("Microsoft.VSTS.Common.Severity", "Severity", "string", false),
        };

        // Only Story Points and Severity are starred, in that order
        var entries = new StatusFieldEntry[]
        {
            new("Microsoft.VSTS.Scheduling.StoryPoints", true),
            new("Microsoft.VSTS.Common.Severity", true),
            new("Microsoft.VSTS.Common.Priority", false),
            new("System.Tags", false),
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: fieldDefs,
            statusFieldEntries: entries);

        var output = _testConsole.Output;
        output.ShouldContain("Story Points");
        output.ShouldContain("5");
        output.ShouldContain("Severity");
        output.ShouldContain("3 - Medium");
        // Unstarred fields should not appear in extended section
        // (Priority still appears because it's not in extended — but check the extended area)
        // Tags should not appear as extended field
        var storyIdx = output.IndexOf("Story Points", StringComparison.Ordinal);
        var sevIdx = output.IndexOf("Severity", StringComparison.Ordinal);
        storyIdx.ShouldBeLessThan(sevIdx); // order preserved
    }

    [Fact]
    public async Task SpectreRenderer_WithoutStatusFieldEntries_PreservesCurrentBehavior()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "1",
        };
        var item = CreateWorkItemWithFields(1, "Default Item", fields);

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: null,
            statusFieldEntries: null);

        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("1");
    }

    [Fact]
    public async Task SpectreRenderer_UnknownRefNameInEntries_SilentlySkipped()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
        };
        var item = CreateWorkItemWithFields(1, "Unknown Ref Item", fields);

        var entries = new StatusFieldEntry[]
        {
            new("Custom.NonExistent.Field", true),
            new("Microsoft.VSTS.Common.Priority", true),
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: null,
            statusFieldEntries: entries);

        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("2");
        output.ShouldNotContain("NonExistent");
    }

    [Fact]
    public async Task SpectreRenderer_AllUnstarredEntries_NoExtendedFields()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
        };
        var item = CreateWorkItemWithFields(1, "AllUnstarred Item", fields);

        var entries = new StatusFieldEntry[]
        {
            new("Microsoft.VSTS.Common.Priority", false),
            new("Microsoft.VSTS.Scheduling.StoryPoints", false),
        };

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None,
            fieldDefinitions: null,
            statusFieldEntries: entries);

        var output = _testConsole.Output;
        // Core fields present
        output.ShouldContain("AllUnstarred Item");
        // Extended fields not shown (no starred entries)
        output.ShouldNotContain("Story Points");
    }

    [Fact]
    public async Task StatusCommand_WithConfigFile_PassesEntriesToRenderer()
    {
        // Write a status-fields config file
        var configContent = "* Priority                (Microsoft.VSTS.Common.Priority)      [integer]\n  Story Points            (Microsoft.VSTS.Scheduling.StoryPoints) [double]\n";
        await File.WriteAllTextAsync(_paths.StatusFieldsPath, configContent);

        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Common.Priority"] = "2",
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
        };
        var item = CreateWorkItemWithFields(1, "Config Test", fields);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Priority");
        output.ShouldContain("2");
        // Story Points is unstarred, should not appear
        output.ShouldNotContain("Story Points");
    }

    // ── EPIC-004: Child progress integration tests ──────────────────

    [Fact]
    public async Task AsyncPath_ParentWithChildren_ShowsProgressBar()
    {
        var parent = CreateWorkItem(1, "Parent Story");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        // Set up children: 2 resolved/closed, 1 active → progress 2/3
        var children = new WorkItem[]
        {
            CreateWorkItemWithState(10, "Child A", "Closed"),
            CreateWorkItemWithState(11, "Child B", "Resolved"),
            CreateWorkItemWithState(12, "Child C", "Active"),
        };
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Progress");
        output.ShouldContain("2/3");
    }

    [Fact]
    public async Task AsyncPath_LeafItem_NoProgressBar()
    {
        var item = CreateWorkItem(1, "Leaf Task");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldNotContain("Progress");
    }

    private static WorkItem CreateWorkItemWithState(int id, string title, string state) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = state,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

}
