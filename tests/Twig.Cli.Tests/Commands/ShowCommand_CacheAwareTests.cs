using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
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
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
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
        _config = new TwigConfiguration();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private ShowCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null, TextWriter? stderr = null) =>
        new(_workItemRepo, _linkRepo, _formatterFactory, _syncCoordinatorFactory, _config,
            pipelineFactory: pipelineFactory, stderr: stderr);

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

    // ── Helpers ──────────────────────────────────────────────────────

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
