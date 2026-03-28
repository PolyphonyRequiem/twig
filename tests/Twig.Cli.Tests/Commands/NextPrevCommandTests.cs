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

public class NextPrevCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;
    private readonly NavigationCommands _navCmd;

    public NextPrevCommandTests()
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
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        var activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        var setCommand = new SetCommand(_workItemRepo, _contextStore, activeItemResolver, syncCoordinator,
            workingSetService, formatterFactory, hintEngine);
        _navCmd = new NavigationCommands(_contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCommand, formatterFactory, activeItemResolver);
    }

    // --- E5-T3: Next sibling tests ---

    [Fact]
    public async Task Next_MovesToNextSibling_ByIdOrder()
    {
        // Parent #100 with children #101, #102, #103; active = #102
        var parent = CreateWorkItem(100, "Parent Feature", parentId: null);
        var child1 = CreateWorkItem(101, "Task A", parentId: 100);
        var child2 = CreateWorkItem(102, "Task B", parentId: 100);
        var child3 = CreateWorkItem(103, "Task C", parentId: 100);

        SetupActiveItem(102, child2);
        _workItemRepo.GetByIdAsync(103, Arg.Any<CancellationToken>()).Returns(child3);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2, child3 });
        _adoService.FetchChildrenAsync(103, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _navCmd.NextAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(103, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Next_AtLastSibling_ReturnsError()
    {
        var child1 = CreateWorkItem(101, "Task A", parentId: 100);
        var child2 = CreateWorkItem(102, "Task B", parentId: 100);
        var child3 = CreateWorkItem(103, "Task C", parentId: 100);

        SetupActiveItem(103, child3);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2, child3 });

        var result = await _navCmd.NextAsync();

        result.ShouldBe(1);
    }

    // --- E5-T4: Prev sibling tests ---

    [Fact]
    public async Task Prev_MovesToPreviousSibling_ByIdOrder()
    {
        var child1 = CreateWorkItem(101, "Task A", parentId: 100);
        var child2 = CreateWorkItem(102, "Task B", parentId: 100);
        var child3 = CreateWorkItem(103, "Task C", parentId: 100);

        SetupActiveItem(102, child2);
        _workItemRepo.GetByIdAsync(101, Arg.Any<CancellationToken>()).Returns(child1);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2, child3 });
        _adoService.FetchChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prev_AtFirstSibling_ReturnsError()
    {
        var child1 = CreateWorkItem(101, "Task A", parentId: 100);
        var child2 = CreateWorkItem(102, "Task B", parentId: 100);

        SetupActiveItem(101, child1);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(1);
    }

    // --- E5-T5: Edge case tests ---

    [Fact]
    public async Task Next_NoParent_ReturnsError()
    {
        var root = CreateWorkItem(1, "Root Item", parentId: null);

        SetupActiveItem(1, root);

        var result = await _navCmd.NextAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Prev_NoParent_ReturnsError()
    {
        var root = CreateWorkItem(1, "Root Item", parentId: null);

        SetupActiveItem(1, root);

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Next_SingleChild_ReturnsError()
    {
        var onlyChild = CreateWorkItem(101, "Only Child", parentId: 100);

        SetupActiveItem(101, onlyChild);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { onlyChild });

        var result = await _navCmd.NextAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Prev_SingleChild_ReturnsError()
    {
        var onlyChild = CreateWorkItem(101, "Only Child", parentId: 100);

        SetupActiveItem(101, onlyChild);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { onlyChild });

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Next_MixedSeedsAndRealItems_SortsByIdCorrectly()
    {
        // Seeds have negative IDs, real items have positive IDs
        var seed1 = CreateWorkItem(-2, "Seed A", parentId: 100, isSeed: true);
        var seed2 = CreateWorkItem(-1, "Seed B", parentId: 100, isSeed: true);
        var real1 = CreateWorkItem(101, "Real Task", parentId: 100);

        // Active = seed at -2, next should be -1
        SetupActiveItem(-2, seed1);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed2);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { seed1, seed2, real1 });
        _adoService.FetchChildrenAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _navCmd.NextAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(-1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prev_MixedSeedsAndRealItems_SortsByIdCorrectly()
    {
        var seed1 = CreateWorkItem(-2, "Seed A", parentId: 100, isSeed: true);
        var seed2 = CreateWorkItem(-1, "Seed B", parentId: 100, isSeed: true);
        var real1 = CreateWorkItem(101, "Real Task", parentId: 100);

        // Active = real #101, prev should be seed #-1
        SetupActiveItem(101, real1);
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed2);
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { seed1, seed2, real1 });
        _adoService.FetchChildrenAsync(-1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(-1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Next_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _navCmd.NextAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Prev_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _navCmd.PrevAsync();

        result.ShouldBe(1);
    }

    private void SetupActiveItem(int id, WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title, int? parentId, bool isSeed = false)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = isSeed ? "" : "New",
            IsSeed = isSeed,
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
