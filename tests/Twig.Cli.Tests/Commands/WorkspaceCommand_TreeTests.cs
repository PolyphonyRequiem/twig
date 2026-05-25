using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
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

/// <summary>
/// Tests for <c>workspace --tree</c> full-backlog tree mode.
/// Covers basic tree mode, output formats, mutual exclusion with --flat,
/// empty workspaces, depth limiting, and multiple-root hierarchies.
/// </summary>
public sealed class WorkspaceCommand_TreeTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationService _iterationService;
    private readonly TwigConfiguration _config;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly ITrackingService _trackingService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public WorkspaceCommand_TreeTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _config = new TwigConfiguration();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);
        _trackingService = Substitute.For<ITrackingService>();
        _trackingService.GetTrackedItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Factory helpers ─────────────────────────────────────────────

    private CommandContext CreateCtx(RenderingPipelineFactory? pipelineFactory = null) =>
        new(pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            _hintEngine,
            _config);

    private WorkspaceCommand CreateCommand() =>
        new(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, _workingSetService, _trackingService,
            new SprintHierarchyBuilder(), new SprintIterationResolver(_iterationService, _workItemRepo));

    private TreeRenderingService CreateTreeRenderingService()
    {
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);

        return new TreeRenderingService(
            CreateCtx(), _contextStore, _workItemRepo,
            _activeItemResolver, _workingSetService,
            syncCoordinatorFactory, _processTypeStore, new Twig.Rendering.RendererFactory());
    }

    private WorkspaceCommand CreateCommandWithTreeService(TreeRenderingService treeService) =>
        new(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, _workingSetService, _trackingService,
            new SprintHierarchyBuilder(), new SprintIterationResolver(_iterationService, _workItemRepo),
            treeService);

    private void SetupSprintItems(params WorkItem[] items)
    {
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(items);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
    }

    private void SetupChildrenAndParents(params WorkItem[] items)
    {
        foreach (var item in items)
        {
            _workItemRepo.GetChildrenAsync(item.Id, Arg.Any<CancellationToken>())
                .Returns(Array.Empty<WorkItem>());
            _workItemRepo.GetParentChainAsync(item.Id, Arg.Any<CancellationToken>())
                .Returns(Array.Empty<WorkItem>());
        }
    }

    private static WorkItem CreateWorkItem(int id, string title) =>
        new()
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

    // ── Mutual exclusion ────────────────────────────────────────────

    [Fact]
    public async Task TreeAndFlat_MutuallyExclusive_ReturnsError()
    {
        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(tree: true, flat: true);
        result.ShouldBe(1);
    }

    // ── No TreeRenderingService ─────────────────────────────────────

    [Fact]
    public async Task Tree_NoTreeRenderingService_ReturnsError()
    {
        var cmd = CreateCommand(); // no TreeRenderingService injected
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        SetupSprintItems(CreateWorkItem(1, "Item 1"));

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(1);
    }

    // ── Basic tree mode (human format) ──────────────────────────────

    [Fact]
    public async Task Tree_HumanFormat_WithSprintItems_Succeeds()
    {
        var item1 = CreateWorkItem(1, "Feature A");
        var item2 = CreateWorkItem(2, "Feature B");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        SetupSprintItems(item1, item2);
        SetupChildrenAndParents(item1, item2);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync("human", tree: true);
        result.ShouldBe(0);
    }

    // ── JSON tree mode ──────────────────────────────────────────────

    [Fact]
    public async Task Tree_JsonFormat_ProducesValidOutput()
    {
        var item = CreateWorkItem(1, "Task A");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync("json", tree: true);
        result.ShouldBe(0);
    }

    // ── Minimal tree mode ───────────────────────────────────────────

    [Fact]
    public async Task Tree_MinimalFormat_ProducesValidOutput()
    {
        var item = CreateWorkItem(1, "Task A");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync("minimal", tree: true);
        result.ShouldBe(0);
    }

    // ── Empty workspace ─────────────────────────────────────────────

    [Fact]
    public async Task Tree_EmptyWorkspace_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        SetupSprintItems(); // no items

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_EmptyWorkspace_JsonFormat_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        SetupSprintItems();

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync("json", tree: true);
        result.ShouldBe(0);
    }

    // ── Multiple roots ──────────────────────────────────────────────

    [Fact]
    public async Task Tree_MultipleSprintItems_RendersEachAsRoot()
    {
        var epic = CreateWorkItem(10, "Epic A");
        epic = new WorkItem
        {
            Id = 10, Type = WorkItemType.Epic, Title = "Epic A", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var story = new WorkItem
        {
            Id = 20, Type = WorkItemType.UserStory, Title = "Story B", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var task = CreateWorkItem(30, "Task C");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(epic);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(story);
        _workItemRepo.GetByIdAsync(30, Arg.Any<CancellationToken>()).Returns(task);
        SetupSprintItems(epic, story, task);
        SetupChildrenAndParents(epic, story, task);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_MultipleRoots_DifferentHierarchies_Succeeds()
    {
        // Two items from different parent hierarchies
        var item1 = new WorkItem
        {
            Id = 10, Type = WorkItemType.UserStory, Title = "Story A", State = "Active",
            ParentId = 100,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var item2 = new WorkItem
        {
            Id = 20, Type = WorkItemType.UserStory, Title = "Story B", State = "Active",
            ParentId = 200,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var parent100 = new WorkItem
        {
            Id = 100, Type = WorkItemType.Epic, Title = "Epic A", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var parent200 = new WorkItem
        {
            Id = 200, Type = WorkItemType.Epic, Title = "Epic B", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(20, Arg.Any<CancellationToken>()).Returns(item2);
        SetupSprintItems(item1, item2);

        _workItemRepo.GetParentChainAsync(10, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(20, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { parent100 });
        _workItemRepo.GetParentChainAsync(200, Arg.Any<CancellationToken>()).Returns(new[] { parent200 });
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    // ── Depth limiting ──────────────────────────────────────────────

    [Fact]
    public async Task Tree_DepthConfig_IsRespected()
    {
        // Set a specific tree depth in config
        _config.Display.TreeDepth = 2;

        var item = CreateWorkItem(1, "Root Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_DepthZero_ReturnsZero()
    {
        _config.Display.TreeDepth = 0;

        var item = CreateWorkItem(1, "Shallow Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_LargeDepth_Succeeds()
    {
        _config.Display.TreeDepth = 100;

        var item = CreateWorkItem(1, "Deep Item");
        var child = CreateWorkItem(2, "Child Item");
        child = new WorkItem
        {
            Id = 2, Type = WorkItemType.Task, Title = "Child Item", State = "New",
            ParentId = 1,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    // ── --all flag in tree mode ──────────────────────────────────────

    [Fact]
    public async Task Tree_AllFlag_ShowsAllTeamItems()
    {
        var myItem = CreateWorkItem(1, "My Item");
        var teamItem = CreateWorkItem(2, "Team Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(myItem);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(teamItem);
        SetupSprintItems(myItem, teamItem);
        SetupChildrenAndParents(myItem, teamItem);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true, all: true);
        result.ShouldBe(0);
    }

    // ── Tree with children hierarchy ────────────────────────────────

    [Fact]
    public async Task Tree_WithChildHierarchy_RendersParentAndChildren()
    {
        var parent = new WorkItem
        {
            Id = 10, Type = WorkItemType.UserStory, Title = "Parent Story", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var child1 = new WorkItem
        {
            Id = 20, Type = WorkItemType.Task, Title = "Child Task 1", State = "New",
            ParentId = 10,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var child2 = new WorkItem
        {
            Id = 21, Type = WorkItemType.Task, Title = "Child Task 2", State = "Active",
            ParentId = 10,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(parent);
        SetupSprintItems(parent);

        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });
        _workItemRepo.GetChildrenAsync(20, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetChildrenAsync(21, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    // ── Tree with no active context ─────────────────────────────────

    [Fact]
    public async Task Tree_NoActiveContext_WithSprintItems_Succeeds()
    {
        var item = CreateWorkItem(1, "Solo Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    // ── Tree skips refresh after first item ──────────────────────────

    [Fact]
    public async Task Tree_MultipleItems_NoRefreshPassedToSubsequentItems()
    {
        var item1 = CreateWorkItem(1, "First");
        var item2 = CreateWorkItem(2, "Second");
        var item3 = CreateWorkItem(3, "Third");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        _workItemRepo.GetByIdAsync(3, Arg.Any<CancellationToken>()).Returns(item3);
        SetupSprintItems(item1, item2, item3);
        SetupChildrenAndParents(item1, item2, item3);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        // This verifies the command doesn't crash when iterating multiple items
        var result = await cmd.ExecuteAsync(tree: true);
        result.ShouldBe(0);
    }

    // ── Tree with noLive flag ───────────────────────────────────────

    [Fact]
    public async Task Tree_NoLiveFlag_Succeeds()
    {
        var item = CreateWorkItem(1, "NoLive Item");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        SetupSprintItems(item);
        SetupChildrenAndParents(item);

        var treeService = CreateTreeRenderingService();
        var cmd = CreateCommandWithTreeService(treeService);

        var result = await cmd.ExecuteAsync(tree: true, noLive: true);
        result.ShouldBe(0);
    }
}
