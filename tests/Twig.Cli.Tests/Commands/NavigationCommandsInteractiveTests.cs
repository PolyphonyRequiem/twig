using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class NavigationCommandsInteractiveTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;
    private readonly INavigationHistoryStore _historyStore;
    private readonly IPromptStateWriter _promptStateWriter;
    private readonly IAsyncRenderer _mockRenderer;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SetCommand _setCommand;
    private readonly HintEngine _hintEngine;

    public NavigationCommandsInteractiveTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
        _historyStore = Substitute.For<INavigationHistoryStore>();
        _promptStateWriter = Substitute.For<IPromptStateWriter>();
        _mockRenderer = Substitute.For<IAsyncRenderer>();

        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
        _workItemLinkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);

        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        var pipelineFactory = new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, _formatterFactory, _hintEngine, new TwigConfiguration());
        var statusFieldReader = new StatusFieldConfigReader(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-navint-test"),
            Path.Combine(Path.GetTempPath(), ".twig-navint-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-navint-test", "twig.db")));
        _setCommand = new SetCommand(ctx, _workItemRepo, _contextStore, _activeItemResolver, syncCoordinatorFactory,
            workingSetService, statusFieldReader);
    }

    // ── Helper factories ────────────────────────────────────────────

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _mockRenderer, isOutputRedirected: () => false);

    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _mockRenderer, isOutputRedirected: () => true);

    private NavigationCommands CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo,
            _setCommand, _formatterFactory, _activeItemResolver,
            pipelineFactory, _historyStore, _promptStateWriter);

    private void SetupActiveItem(int id, string title, int? parentId = null)
    {
        var item = CreateWorkItem(id, title, parentId);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        if (parentId.HasValue)
        {
            var parent = CreateWorkItem(parentId.Value, $"Parent of {title}", null);
            _workItemRepo.GetByIdAsync(parentId.Value, Arg.Any<CancellationToken>()).Returns(parent);
            _workItemRepo.GetParentChainAsync(parentId.Value, Arg.Any<CancellationToken>())
                .Returns(new[] { parent });
            _workItemRepo.GetChildrenAsync(parentId.Value, Arg.Any<CancellationToken>())
                .Returns(new[] { item });
        }
    }

    // ── Commit path ─────────────────────────────────────────────────

    [Fact]
    public async Task InteractiveAsync_CommitPath_SetsContextAndRecordsHistory()
    {
        SetupActiveItem(10, "Active Feature", parentId: 1);
        _mockRenderer.RenderInteractiveTreeAsync(
                Arg.Any<TreeNavigatorState>(),
                Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
                Arg.Any<CancellationToken>())
            .Returns(42);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
        await _historyStore.Received(1).RecordVisitAsync(42, Arg.Any<CancellationToken>());
        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ── Cancel path ─────────────────────────────────────────────────

    [Fact]
    public async Task InteractiveAsync_CancelPath_NoStoreChanges()
    {
        SetupActiveItem(10, "Active Feature", parentId: 1);
        _mockRenderer.RenderInteractiveTreeAsync(
                Arg.Any<TreeNavigatorState>(),
                Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
                Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _historyStore.DidNotReceive().RecordVisitAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _promptStateWriter.DidNotReceive().WritePromptStateAsync();
    }

    // ── Non-TTY fallback ────────────────────────────────────────────

    [Fact]
    public async Task InteractiveAsync_NonTty_PrintsFallbackHelpText()
    {
        var cmd = CreateCommand(pipelineFactory: null);
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _mockRenderer.DidNotReceive().RenderInteractiveTreeAsync(
            Arg.Any<TreeNavigatorState>(),
            Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractiveAsync_RedirectedOutput_PrintsFallbackHelpText()
    {
        // No active item setup needed — redirected output returns before consulting IContextStore
        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _mockRenderer.DidNotReceive().RenderInteractiveTreeAsync(
            Arg.Any<TreeNavigatorState>(),
            Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
            Arg.Any<CancellationToken>());
    }

    // ── Empty active context ────────────────────────────────────────

    [Fact]
    public async Task InteractiveAsync_NoActiveContext_ReturnsGracefully()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _mockRenderer.DidNotReceive().RenderInteractiveTreeAsync(
            Arg.Any<TreeNavigatorState>(),
            Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractiveAsync_ActiveItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(1);
        await _mockRenderer.DidNotReceive().RenderInteractiveTreeAsync(
            Arg.Any<TreeNavigatorState>(),
            Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
            Arg.Any<CancellationToken>());
    }

    // ── loadNodeState callback ──────────────────────────────────────

    [Fact]
    public async Task InteractiveAsync_LoadNodeState_CallsAllRepositories()
    {
        var active = CreateWorkItem(10, "Active", parentId: 5);
        var parent = CreateWorkItem(5, "Parent", parentId: null);
        var child = CreateWorkItem(20, "Child of 10", parentId: 10);
        var sibling = CreateWorkItem(11, "Sibling", parentId: 5);
        var links = new[] { new WorkItemLink(10, 100, LinkTypes.Related) };
        var seedLinks = new[] { new SeedLink(-1, 10, SeedLinkTypes.Successor, DateTimeOffset.UtcNow) };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIdAsync(5, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(5, Arg.Any<CancellationToken>()).Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>()).Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(5, Arg.Any<CancellationToken>()).Returns(new[] { active, sibling });
        _workItemLinkRepo.GetLinksAsync(10, Arg.Any<CancellationToken>()).Returns(links);
        _seedLinkRepo.GetLinksForItemAsync(10, Arg.Any<CancellationToken>()).Returns(seedLinks);

        // Capture the loadNodeState callback
        Func<int, Task<TreeNavigatorState>>? capturedCallback = null;
        _mockRenderer.RenderInteractiveTreeAsync(
                Arg.Any<TreeNavigatorState>(),
                Arg.Do<Func<int, Task<TreeNavigatorState>>>(f => capturedCallback = f),
                Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        await cmd.InteractiveAsync();

        // Verify the initial load called all repos for item 10
        await _workItemRepo.Received().GetByIdAsync(10, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetParentChainAsync(5, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetChildrenAsync(10, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetChildrenAsync(5, Arg.Any<CancellationToken>());
        await _workItemLinkRepo.Received().GetLinksAsync(10, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received().GetLinksForItemAsync(10, Arg.Any<CancellationToken>());

        // Now invoke the captured callback with a different ID to verify it calls repos correctly
        capturedCallback.ShouldNotBeNull();

        var itemB = CreateWorkItem(20, "Child of 10", parentId: 10);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(itemB);
        _workItemRepo.GetParentChainAsync(10, Arg.Any<CancellationToken>()).Returns(new[] { parent, active });
        _workItemRepo.GetChildrenAsync(20, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemLinkRepo.GetLinksAsync(20, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItemLink>());
        _seedLinkRepo.GetLinksForItemAsync(20, Arg.Any<CancellationToken>()).Returns(Array.Empty<SeedLink>());

        var state = await capturedCallback(20);

        state.ShouldNotBeNull();
        state.CursorItem.ShouldNotBeNull();
        state.CursorItem!.Id.ShouldBe(20);
        await _workItemRepo.Received().GetByIdAsync(20, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetParentChainAsync(10, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().GetChildrenAsync(20, Arg.Any<CancellationToken>());
        await _workItemLinkRepo.Received().GetLinksAsync(20, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received().GetLinksForItemAsync(20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InteractiveAsync_LoadNodeState_RootItem_HandlesSiblingsGracefully()
    {
        var root = CreateWorkItem(1, "Root Item", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        TreeNavigatorState? capturedState = null;
        _mockRenderer.RenderInteractiveTreeAsync(
                Arg.Do<TreeNavigatorState>(s => capturedState = s),
                Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
                Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        await cmd.InteractiveAsync();

        capturedState.ShouldNotBeNull();
        capturedState!.CursorItem.ShouldNotBeNull();
        capturedState.CursorItem!.Id.ShouldBe(1);
        capturedState.ParentChain.Count.ShouldBe(0);
        capturedState.VisibleSiblings.Count.ShouldBe(1);
        capturedState.VisibleSiblings[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task InteractiveAsync_CommitPath_WithNullHistoryStore_DoesNotThrow()
    {
        SetupActiveItem(10, "Active Feature", parentId: 1);
        _mockRenderer.RenderInteractiveTreeAsync(
                Arg.Any<TreeNavigatorState>(),
                Arg.Any<Func<int, Task<TreeNavigatorState>>>(),
                Arg.Any<CancellationToken>())
            .Returns(42);

        // Create command without history store or prompt state writer
        var cmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo,
            _setCommand, _formatterFactory, _activeItemResolver,
            CreateTtyPipelineFactory(), historyStore: null, promptStateWriter: null);
        var result = await cmd.InteractiveAsync();

        result.ShouldBe(0);
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    // ── Helper ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId)
    {
        return new WorkItem
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
}
