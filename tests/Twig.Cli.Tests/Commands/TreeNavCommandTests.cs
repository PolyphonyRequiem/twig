using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TreeNavCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly SetCommand _setCommand;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;

    public TreeNavCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
        _workItemLinkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _config = new TwigConfiguration();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        _syncCoordinator = syncCoordinator;
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _workingSetService = workingSetService;
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _setCommand = new SetCommand(_workItemRepo, _contextStore, _activeItemResolver, syncCoordinator,
            workingSetService, _formatterFactory, _hintEngine);
    }

    [Fact]
    public async Task Tree_DisplaysHierarchy()
    {
        var parent = CreateWorkItem(1, "Parent Feature", parentId: null);
        var active = CreateWorkItem(2, "Active Story", parentId: 1);
        var child1 = CreateWorkItem(3, "Child Task 1", parentId: 2);
        var child2 = CreateWorkItem(4, "Child Task 2", parentId: 2);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var treeCmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator, _processTypeStore);
        var result = await treeCmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_EmptyState_DoesNotCrash()
    {
        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Seed Item",
            State = "",
            IsSeed = true,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var active = CreateWorkItem(1, "Active", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { seed });

        var treeCmd = new TreeCommand(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver, _workingSetService, _syncCoordinator, _processTypeStore);
        var result = await treeCmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Up_AtRoot_ReturnsError()
    {
        var root = CreateWorkItem(1, "Root Item", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(root);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.UpAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Up_NavigatesToParent()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child = CreateWorkItem(2, "Child", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(2);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.UpAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_WithPattern_NavigatesToChild()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child = CreateWorkItem(2, "Fix login bug", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.DownAsync("login");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_NoMatch_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.DownAsync("nonexistent");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Down_NoArg_NoChildren_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.DownAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Down_NoArg_SingleChild_AutoNavigates()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child = CreateWorkItem(2, "Only Child", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        _adoService.FetchChildrenAsync(2, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, _setCommand, _formatterFactory, _activeItemResolver);
        var result = await navCmd.DownAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(2, Arg.Any<CancellationToken>());
    }

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
