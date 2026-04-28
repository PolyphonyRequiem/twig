using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
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

public class TreeCommandAsyncTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorFactory _syncCoordinatorFactory;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public TreeCommandAsyncTests()
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
        _syncCoordinatorFactory = new SyncCoordinatorFactory(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
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

    private CommandContext CreateCtx(RenderingPipelineFactory? pipelineFactory = null) =>
        new(pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            new HintEngine(new DisplayConfig { Hints = false }),
            _config);

    private TreeCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(CreateCtx(pipelineFactory), _contextStore, _workItemRepo, _activeItemResolver,
            _workingSetService, _syncCoordinatorFactory, _processTypeStore);

    // ── Sibling count rendering tests ───────────────────────────────

    [Fact]
    public async Task AsyncPath_SiblingCount_DimmedOutputForParentChain()
    {
        var parent = CreateWorkItem(100, "Epic Parent", type: "Epic", parentId: 50);
        var focus = CreateWorkItem(1, "Focus Task", parentId: 100);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        // Parent's siblings (children of parent's parent)
        _workItemRepo.GetChildrenAsync(50, Arg.Any<CancellationToken>()).Returns(new[]
        {
            parent,
            CreateWorkItem(101, "Sibling Epic", parentId: 50),
            CreateWorkItem(102, "Another Epic", parentId: 50),
        });
        // Focus's siblings (children of focus's parent)
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>()).Returns(new[]
        {
            focus,
            CreateWorkItem(2, "Sibling Task", parentId: 100),
        });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // Should contain sibling counts as dimmed text
        output.ShouldContain("...3"); // parent's sibling count (3 children of ID 50)
        output.ShouldContain("...2"); // focused item's sibling count (2 children of ID 100)
    }

    [Fact]
    public async Task AsyncPath_SiblingCount_RootNodeOmitsSiblingCount()
    {
        var focus = CreateWorkItem(1, "Root Focus");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // Root node should not show any sibling count indicator (e.g., ...N or ...?)
        output.ShouldNotMatch(@"\.\.\.\d");
        output.ShouldNotContain("...?");
    }

    [Fact]
    public async Task AsyncPath_SiblingCount_ParentChainRootHasNoParent()
    {
        var root = CreateWorkItem(100, "Root Epic", type: "Epic"); // no parent
        var focus = CreateWorkItem(1, "Focus Task", parentId: 100);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { root });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());
        // Focus's siblings
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>()).Returns(new[]
        {
            focus,
            CreateWorkItem(2, "Sibling", parentId: 100),
            CreateWorkItem(3, "Another Sibling", parentId: 100),
        });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // Focus should show sibling count (3)
        output.ShouldContain("...3");
    }

    // ── SpectreRenderer unit tests ──────────────────────────────────

    [Fact]
    public void FormatSiblingCount_KnownCount_ReturnsDimmedMarkup()
    {
        var result = SpectreRenderer.FormatSiblingCount(5);
        result.ShouldBe("[dim]...5[/]");
    }

    // ── BuildSpectreTreeAsync direct unit tests ─────────────────────

    [Fact]
    public async Task BuildSpectreTreeAsync_NoParentChain_RootFocus_NoSiblingIndicator()
    {
        var focus = CreateWorkItem(1, "Root Focus");

        var (tree, container) = await _spectreRenderer.BuildSpectreTreeAsync(
            focus, Array.Empty<WorkItem>(), activeId: 1,
            getSiblingCount: _ => Task.FromResult<int?>(null));

        tree.ShouldNotBeNull();
        tree.ShouldBe(container); // focus is root — tree IS the container
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_WithParentChain_ReturnsCorrectStructure()
    {
        var root = CreateWorkItem(100, "Root Epic", type: "Epic");
        var mid = CreateWorkItem(50, "Mid Feature", parentId: 100, type: "Feature");
        var focus = CreateWorkItem(1, "Focus Task", parentId: 50);
        var parentChain = new[] { root, mid };

        var counts = new Dictionary<int, int?> { [100] = null, [50] = 3, [1] = 2 };

        var (tree, focusContainer) = await _spectreRenderer.BuildSpectreTreeAsync(
            focus, parentChain, activeId: 1,
            getSiblingCount: id => Task.FromResult(counts.GetValueOrDefault(id)));

        tree.ShouldNotBeNull();
        focusContainer.ShouldNotBeNull();
        // focusContainer is NOT the tree root (focus is nested under parents)
        focusContainer.ShouldNotBe(tree);
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_NullCallback_NoSiblingIndicators()
    {
        var parent = CreateWorkItem(100, "Parent", parentId: 50, type: "Epic");
        var focus = CreateWorkItem(1, "Focus Task", parentId: 100);

        var (tree, _) = await _spectreRenderer.BuildSpectreTreeAsync(
            focus, new[] { parent }, activeId: 1,
            getSiblingCount: null);

        tree.ShouldNotBeNull();
        // Render to string and verify no sibling count markup is present
        var console = new TestConsole();
        console.Write(tree);
        console.Output.ShouldNotContain("...");
    }

    [Fact]
    public async Task BuildSpectreTreeAsync_NullCount_OmitsIndicator()
    {
        // Parent HAS a parentId so the getSiblingCount callback is invoked,
        // but the callback returns null to exercise the count.HasValue guard.
        var parent = CreateWorkItem(100, "Mid Parent", parentId: 50, type: "Epic");
        var focus = CreateWorkItem(1, "Focus Task", parentId: 100);

        var callbackInvoked = false;
        var (tree, _) = await _spectreRenderer.BuildSpectreTreeAsync(
            focus, new[] { parent }, activeId: 1,
            getSiblingCount: id =>
            {
                if (id == 100) callbackInvoked = true;
                // Return null for all nodes to exercise the count.HasValue check
                return Task.FromResult<int?>(null);
            });

        // The callback should have been invoked for the parent chain node
        callbackInvoked.ShouldBeTrue();
        tree.ShouldNotBeNull();
        // Render to string and verify no sibling count indicator is present
        var console = new TestConsole();
        console.Write(tree);
        console.Output.ShouldNotContain("...");
    }

    // ── EPIC-002: State-colored │ prefix on child nodes ────────────

    [Fact]
    public async Task AsyncPath_ChildNodeLabel_ContainsColoredVerticalBar()
    {
        var focus = CreateWorkItem(1, "Focus Task");
        var activeChild = CreateWorkItem(2, "Active Child", state: "Active");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { activeChild });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // Active → InProgress → blue; Spectre markup [blue]│[/] should render │ in the output
        output.ShouldContain("│");
        output.ShouldContain("Active Child");
    }

    [Fact]
    public async Task AsyncPath_ChildNodeLabel_ClosedState_ContainsVerticalBar()
    {
        var focus = CreateWorkItem(1, "Focus Task");
        var closedChild = CreateWorkItem(2, "Closed Child", state: "Closed");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { closedChild });

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        // Closed → Completed → green; should show │ prefix on child label
        output.ShouldContain("│");
        output.ShouldContain("Closed Child");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string type = "Task", string state = "New")
    {
        return new WorkItem
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
}
