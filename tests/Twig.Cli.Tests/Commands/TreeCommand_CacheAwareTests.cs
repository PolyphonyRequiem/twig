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

public sealed class TreeCommand_CacheAwareTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public TreeCommand_CacheAwareTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _config = new TwigConfiguration();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private TreeCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver,
            _workingSetService, _syncCoordinator, _processTypeStore, pipelineFactory);

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    // ── --no-refresh flag: skips sync pass ──────────────────────────

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersTreeFromCacheOnly()
    {
        var item = CreateWorkItem(1, "Cached Tree Item");
        SetupActiveItem(item);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Cached Tree Item");

        // Verify ADO service was never called for sync (no fetch attempts)
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(1);
    }

    [Fact]
    public async Task NoRefresh_WithChildren_RendersTreeWithChildren()
    {
        var focus = CreateWorkItem(1, "Parent Item");
        var child1 = CreateWorkItem(2, "Child One", parentId: 1);
        var child2 = CreateWorkItem(3, "Child Two", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Parent Item");
        output.ShouldContain("Child One");
        output.ShouldContain("Child Two");

        // No sync should have occurred
        await _adoService.DidNotReceive().FetchAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_IsIndependentOfNoLive()
    {
        var item = CreateWorkItem(1, "Independent Flag Item");
        SetupActiveItem(item);

        // Both flags set: noLive + noRefresh
        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true, noRefresh: true);

        result.ShouldBe(0);
    }

    // ── Default path: two-pass rendering ────────────────────────────

    [Fact]
    public async Task Default_TwoPassRendering_RendersCachedTreeThenSyncs()
    {
        var item = CreateWorkItem(1, "Two Pass Tree Item");
        SetupActiveItem(item);

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        // noRefresh defaults to false — should use RenderWithSyncAsync
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("Two Pass Tree Item");
    }

    [Fact]
    public async Task Default_TwoPass_ShowsSyncStatusIndicator()
    {
        var item = CreateWorkItem(1, "Sync Indicator Item");
        SetupActiveItem(item);

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // The RenderWithSyncAsync should show sync status messages
        // Either "✓ up to date" or "⟳ syncing..." should appear in output
        (output.Contains("up to date") || output.Contains("syncing")).ShouldBeTrue(
            "Expected sync status indicator in output");
    }

    [Fact]
    public async Task Default_SyncFailure_FallsBackToDirectTreeRender()
    {
        var item = CreateWorkItem(1, "Fallback Tree Item", parentId: 99);
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

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        // Should still render the tree (via fallback)
        var output = _testConsole.Output;
        output.ShouldContain("Fallback Tree Item");
    }

    // ── Cache-age indicators ────────────────────────────────────────

    [Fact]
    public async Task TwoPass_StaleItem_ShowsCacheAgeIndicator()
    {
        // Create item with old LastSyncedAt to trigger cache-age display
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

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("cached");
        output.ShouldContain("ago");
    }

    [Fact]
    public async Task TwoPass_FreshItem_NoCacheAgeIndicator()
    {
        // Create item with recent LastSyncedAt — should NOT show cache-age
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

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task TwoPass_StaleChildren_ShowCacheAgeSuffix()
    {
        var focus = CreateWorkItem(1, "Focus Item");
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

        _spectreRenderer.SyncStatusDelay = TimeSpan.Zero;

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Stale Child");
        // Should show cache-age suffix for the stale child (2h old)
        output.ShouldContain("cached 2h ago");
    }

    // ── BuildTreeViewAsync unit tests ───────────────────────────────

    [Fact]
    public async Task BuildTreeViewAsync_NoChildren_ReturnsTreeRenderable()
    {
        var focus = CreateWorkItem(1, "Focus Only");
        var result = await _spectreRenderer.BuildTreeViewAsync(
            focus,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            maxChildren: 10,
            activeId: 1);

        result.ShouldNotBeNull();

        var console = new TestConsole();
        console.Write(result);
        console.Output.ShouldContain("Focus Only");
    }

    [Fact]
    public async Task BuildTreeViewAsync_WithChildren_IncludesChildNodes()
    {
        var focus = CreateWorkItem(1, "Parent Focus");
        var child = CreateWorkItem(2, "Child Node", parentId: 1);

        var result = await _spectreRenderer.BuildTreeViewAsync(
            focus,
            Array.Empty<WorkItem>(),
            new[] { child },
            maxChildren: 10,
            activeId: 1);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldContain("Parent Focus");
        output.ShouldContain("Child Node");
    }

    [Fact]
    public async Task BuildTreeViewAsync_WithParentChain_RendersHierarchy()
    {
        var root = CreateWorkItem(100, "Root Epic", type: "Epic");
        var focus = CreateWorkItem(1, "Focused Task", parentId: 100);

        var result = await _spectreRenderer.BuildTreeViewAsync(
            focus,
            new[] { root },
            Array.Empty<WorkItem>(),
            maxChildren: 10,
            activeId: 1);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldContain("Root Epic");
        output.ShouldContain("Focused Task");
    }

    [Fact]
    public async Task BuildTreeViewAsync_MaxChildrenExceeded_ShowsMoreIndicator()
    {
        var focus = CreateWorkItem(1, "Focus");
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        var result = await _spectreRenderer.BuildTreeViewAsync(
            focus,
            Array.Empty<WorkItem>(),
            children,
            maxChildren: 2,
            activeId: 1);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldContain("... and 3 more");
    }

    [Fact]
    public async Task BuildTreeViewAsync_WithLinks_RendersLinksSection()
    {
        var focus = CreateWorkItem(1, "Linked Item");
        var links = new[]
        {
            new WorkItemLink(1, 42, "Related"),
        };

        var result = await _spectreRenderer.BuildTreeViewAsync(
            focus,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            maxChildren: 10,
            activeId: 1,
            links: links);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldContain("Links");
        output.ShouldContain("Related");
        output.ShouldContain("#42");
    }

    [Fact]
    public async Task BuildTreeViewAsync_StaleItem_ShowsCacheAge()
    {
        var staleItem = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Parse("Task").Value,
            Title = "Stale Focus",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
        };

        var result = await _spectreRenderer.BuildTreeViewAsync(
            staleItem,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            maxChildren: 10,
            activeId: 1,
            cacheStaleMinutes: 5);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldContain("cached 15m ago");
    }

    [Fact]
    public async Task BuildTreeViewAsync_FreshItem_NoCacheAge()
    {
        var freshItem = new WorkItem
        {
            Id = 1,
            Type = WorkItemType.Parse("Task").Value,
            Title = "Fresh Focus",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
            LastSyncedAt = DateTimeOffset.UtcNow,
        };

        var result = await _spectreRenderer.BuildTreeViewAsync(
            freshItem,
            Array.Empty<WorkItem>(),
            Array.Empty<WorkItem>(),
            maxChildren: 10,
            activeId: 1,
            cacheStaleMinutes: 5);

        var console = new TestConsole();
        console.Write(result);
        var output = console.Output;
        output.ShouldNotContain("cached");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string type = "Task", string state = "New") =>
        new()
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = state,
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
}
