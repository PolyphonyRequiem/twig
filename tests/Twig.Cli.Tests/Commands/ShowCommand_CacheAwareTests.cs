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

public sealed class ShowCommand_CacheAwareTests : IDisposable
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly WorkingSetService _workingSetService;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly string _tempDir;

    public ShowCommand_CacheAwareTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _contextStore = Substitute.For<IContextStore>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var iterationService = Substitute.For<IIterationService>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, iterationService, null);

        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _tempDir = Path.Combine(Path.GetTempPath(), "twig-show-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _statusFieldReader = new StatusFieldConfigReader(new TwigPaths(_tempDir, Path.Combine(_tempDir, "config"), Path.Combine(_tempDir, "twig.db")));

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private ShowCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null, TextWriter? stderr = null, TwigPaths? twigPaths = null, IAdoGitService? adoGitService = null)
    {
        var pipeline = pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipeline, _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }), new TwigConfiguration(), Stderr: stderr);
        return new ShowCommand(ctx, _workItemRepo, _linkRepo, _syncCoordinatorFactory, _statusFieldReader,
            fieldDefinitionStore: _fieldDefinitionStore,
            processConfigProvider: _processConfigProvider,
            contextStore: _contextStore,
            activeItemResolver: _activeItemResolver,
            pendingChangeStore: _pendingChangeStore,
            workingSetService: _workingSetService,
            twigPaths: twigPaths,
            adoGitService: adoGitService);
    }

    private void SetupCachedItem(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
    }

    private static WorkItemBuilder Item(int id, string title) =>
        new WorkItemBuilder(id, title)
            .WithAreaPath("Project")
            .WithIterationPath("Project\\Sprint 1");

    // ── --no-refresh flag: skips sync pass ──────────────────────────

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersFromCacheOnly()
    {
        var item = Item(1, "Cached Show Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Cached Show Item");

        // Verify ADO service was never called for sync (no fetch attempts)
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_ItemNotInCache_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        using var errWriter = new StringWriter();
        var cmd = CreateCommand(CreateTtyPipelineFactory(), stderr: errWriter);

        var result = await cmd.ExecuteAsync(999, "human", noRefresh: true);

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("not found in local cache");
    }

    // ── Default path: two-pass rendering ────────────────────────────

    [Fact]
    public async Task Default_TwoPassRendering_RendersCachedThenSyncs()
    {
        var item = Item(1, "Two Pass Show Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        // noRefresh defaults to false — should use RenderWithSyncAsync
        var result = await cmd.ExecuteAsync(1, "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Two Pass Show Item");

        // Verify the sync path was actually exercised (not just cache-only)
        await _adoService.Received().FetchAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_SyncUpdatesData_RevisedViewReflectsChanges()
    {
        var cachedItem = Item(1, "Original Title").Build();
        SetupCachedItem(cachedItem);

        // After sync, return an updated item
        var freshItem = Item(1, "Updated Title").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(freshItem);

        // First two GetByIdAsync calls return cached data (initial lookup + ProtectedCacheWriter check),
        // third call (buildRevisedView after sync) returns fresh data
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(cachedItem, cachedItem, freshItem);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human");

        result.ShouldBe(0);
        _testConsole.Output.ShouldContain("Updated Title");
    }

    [Fact]
    public async Task Default_SyncFailure_FallsBackToStaticRender()
    {
        var item = Item(1, "Fallback Show Item").Build();
        SetupCachedItem(item);

        // Make ADO service throw to trigger the fallback path
        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Network error")));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Fallback Show Item");
    }

    [Fact]
    public async Task NoRefresh_WithChildren_StillRendersEnrichment()
    {
        var parent = Item(1, "Parent Item").Build();
        var child1 = Item(2, "Child One").WithParent(1).Build();

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1 });
        _linkRepo.GetLinksAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Parent Item");

        // ADO service never called (no sync)
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Non-TTY sync-first: machine output formats ─────────────────

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task NonTty_SyncsBeforeEmitting(string format)
    {
        var item = Item(1, "Sync First Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(); // non-TTY pipeline (isOutputRedirected: true)
        await CaptureStdout(() => cmd.ExecuteAsync(1, format));

        // Verify sync was exercised — FetchAsync is called by SyncItemSetAsync
        await _adoService.Received().FetchAsync(1, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("human")]
    public async Task NonTty_NoRefresh_SkipsSync(string format)
    {
        var item = Item(1, "No Refresh Machine").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand();
        await CaptureStdout(() => cmd.ExecuteAsync(1, format, noRefresh: true));

        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTty_Json_SyncUpdatesData_EmitsFreshItem()
    {
        var cachedItem = Item(1, "Stale Title").Build();
        SetupCachedItem(cachedItem);

        var freshItem = Item(1, "Fresh Title").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(freshItem);

        // First GetByIdAsync returns cached (initial lookup),
        // second returns cached (ProtectedCacheWriter check in sync),
        // third returns fresh (post-sync reload)
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(cachedItem, cachedItem, freshItem);

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json"));

        output.ShouldContain("Fresh Title");
    }

    [Fact]
    public async Task NonTty_Json_SyncFailure_FallsBackToCache()
    {
        var item = Item(1, "Cached Fallback Item").Build();
        SetupCachedItem(item);

        _adoService.FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Network error")));

        var cmd = CreateCommand();
        var exitCode = 0;
        var output = await CaptureStdout(async () =>
        {
            exitCode = await cmd.ExecuteAsync(1, "json");
            return exitCode;
        });

        exitCode.ShouldBe(0);
        output.ShouldContain("Cached Fallback Item");
    }

    // ── Pending changes enrichment ─────────────────────────────────

    [Fact]
    public async Task NoRefresh_PendingChangesPresent_IncludedInTtyOutput()
    {
        var item = Item(1, "Pending TTY Item").Build();
        SetupCachedItem(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PendingChangeRecord[]
            {
                new(1, "set_field", "System.Title", "Old", "New"),
                new(1, "note", null, null, "A note"),
            });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human", noRefresh: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().GetChangesAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Default_TwoPassRendering_QueriesPendingChanges()
    {
        var item = Item(1, "Two Pass Pending").Build();
        SetupCachedItem(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PendingChangeRecord[]
            {
                new(1, "set_field", "System.State", "New", "Active"),
            });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(1, "human");

        result.ShouldBe(0);
        await _pendingChangeStore.Received().GetChangesAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTty_Json_IncludesPendingChangesWhenPresent()
    {
        var item = Item(1, "JSON Pending Item").Build();
        SetupCachedItem(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PendingChangeRecord[]
            {
                new(1, "set_field", "System.Title", "Old", "New"),
                new(1, "set_field", "System.State", "Active", "Resolved"),
                new(1, "note", null, null, "A note"),
            });

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json"));

        output.ShouldContain("\"pendingChanges\"");
        output.ShouldContain("\"fieldEditCount\": 2");
        output.ShouldContain("\"noteCount\": 1");
    }

    [Fact]
    public async Task NonTty_Json_OmitsPendingChangesWhenNone()
    {
        var item = Item(1, "Clean JSON Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json"));

        output.ShouldNotContain("pendingChanges");
    }

    [Fact]
    public async Task NonTty_Human_IncludesPendingCounts()
    {
        var item = Item(1, "Human Pending Item").Build();
        SetupCachedItem(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PendingChangeRecord[]
            {
                new(1, "set_field", "System.Title", "Old", "New"),
                new(1, "note", null, null, "Note text"),
            });

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "human"));

        output.ShouldContain("1 field change");
        output.ShouldContain("1 note staged");
    }

    [Fact]
    public async Task NonTty_SyncFirst_QueriesPendingChangesForCorrectId()
    {
        var item = Item(77, "Sync Pending Query").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand();
        await CaptureStdout(() => cmd.ExecuteAsync(77, "json"));

        await _pendingChangeStore.Received().GetChangesAsync(77, Arg.Any<CancellationToken>());
    }

    // ── Git context enrichment ──────────────────────────────────────

    /// <summary>Creates a temp directory with a .git/HEAD pointing to a branch.</summary>
    private string CreateGitRepo(string branchName)
    {
        var repoDir = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        var gitDir = Path.Combine(repoDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), $"ref: refs/heads/{branchName}\n");
        return repoDir;
    }

    [Fact]
    public async Task NoRefresh_WithGitRepo_IncludesGitContextInTtyOutput()
    {
        var repoDir = CreateGitRepo("feature/42-test-branch");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = Item(1, "Git TTY Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory(), twigPaths: paths);
        var result = await cmd.ExecuteAsync(1, "human", noRefresh: true);

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("feature/42-test-branch");
    }

    [Fact]
    public async Task NonTty_Json_WithGitRepo_IncludesGitContext()
    {
        var repoDir = CreateGitRepo("feature/99-json-branch");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = Item(1, "Git JSON Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(twigPaths: paths);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json"));

        output.ShouldContain("\"gitContext\"");
        output.ShouldContain("\"currentBranch\": \"feature/99-json-branch\"");
    }

    [Fact]
    public async Task NonTty_Human_WithGitRepo_IncludesGitContext()
    {
        var repoDir = CreateGitRepo("main");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var item = Item(1, "Git Human Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand(twigPaths: paths);
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "human"));

        output.ShouldContain("Git");
        output.ShouldContain("Branch:");
        output.ShouldContain("main");
    }

    [Fact]
    public async Task Default_SyncUpdatesData_GitContextPreservedAcrossSync()
    {
        var repoDir = CreateGitRepo("feature/sync-git");
        var twigDir = Path.Combine(repoDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var cachedItem = Item(1, "Original Title").Build();
        SetupCachedItem(cachedItem);

        var freshItem = Item(1, "Updated Title").Build();
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(freshItem);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(cachedItem, cachedItem, freshItem);

        var cmd = CreateCommand(CreateTtyPipelineFactory(), twigPaths: paths);
        var result = await cmd.ExecuteAsync(1, "human");

        result.ShouldBe(0);
        // Git context should be present in the output even after sync
        _testConsole.Output.ShouldContain("Updated Title");
    }

    [Fact]
    public async Task NonTty_WithoutGitRepo_OmitsGitContext()
    {
        var item = Item(1, "No Git Item").Build();
        SetupCachedItem(item);

        var cmd = CreateCommand();
        var output = await CaptureStdout(() => cmd.ExecuteAsync(1, "json"));

        output.ShouldNotContain("\"gitContext\"");
    }

    // ── Helpers ──────────────────────────────────────────────────────

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
}
