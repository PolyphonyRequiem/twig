using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ShowCommand_CacheAwareTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IWorkItemLinkRepository _linkRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StatusFieldConfigReader _statusFieldReader;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public ShowCommand_CacheAwareTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _linkRepo = Substitute.For<IWorkItemLinkRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        var tempDir = Path.Combine(Path.GetTempPath(), "twig-show-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        _statusFieldReader = new StatusFieldConfigReader(new TwigPaths(tempDir, Path.Combine(tempDir, "config"), Path.Combine(tempDir, "twig.db")));

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private ShowCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null, TextWriter? stderr = null)
    {
        var pipeline = pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipeline, _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }), new TwigConfiguration(), Stderr: stderr);
        return new ShowCommand(ctx, _workItemRepo, _linkRepo, _syncCoordinatorFactory, _statusFieldReader);
    }

    private void SetupCachedItem(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _linkRepo.GetLinksAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
    }

    // ── --no-refresh flag: skips sync pass ──────────────────────────

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersFromCacheOnly()
    {
        var item = CreateWorkItem(1, "Cached Show Item");
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
        var item = CreateWorkItem(1, "Two Pass Show Item");
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
        var cachedItem = CreateWorkItem(1, "Original Title");
        SetupCachedItem(cachedItem);

        // After sync, return an updated item
        var freshItem = CreateWorkItem(1, "Updated Title");
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
        var item = CreateWorkItem(1, "Fallback Show Item");
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
        var parent = CreateWorkItem(1, "Parent Item");
        var child1 = CreateWorkItem(2, "Child One", parentId: 1);

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
        var item = CreateWorkItem(1, "Sync First Item");
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
        var item = CreateWorkItem(1, "No Refresh Machine");
        SetupCachedItem(item);

        var cmd = CreateCommand();
        await CaptureStdout(() => cmd.ExecuteAsync(1, format, noRefresh: true));

        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTty_Json_SyncUpdatesData_EmitsFreshItem()
    {
        var cachedItem = CreateWorkItem(1, "Stale Title");
        SetupCachedItem(cachedItem);

        var freshItem = CreateWorkItem(1, "Fresh Title");
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
        var item = CreateWorkItem(1, "Cached Fallback Item");
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

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        ParentId = parentId,
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
