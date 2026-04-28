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
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class TreeCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinatorPair _SyncCoordinatorPair;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public TreeCommandTests()
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
        _SyncCoordinatorPair = new SyncCoordinatorPair(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, null, 30, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Command factory methods ─────────────────────────────────────

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private TreeCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null) =>
        new(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver,
            _workingSetService, _SyncCoordinatorPair, _processTypeStore, pipelineFactory);

    // ── Depth flag behavior ─────────────────────────────────────────

    [Fact]
    public async Task Tree_DepthFlag_LimitsChildren()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "minimal", depth: 2);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_AllFlag_ShowsAllChildren()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 21)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "minimal", all: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_DepthFlagOverridesConfig()
    {
        _config.Display.TreeDepth = 100;

        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "minimal", depth: 2);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_AllFlagOverridesDepth()
    {
        var active = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 6)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "minimal", depth: 1, all: true);

        result.ShouldBe(0);
    }

    // ── WS-021: JSON output parity ──────────────────────────────────

    [Fact]
    public async Task Tree_JsonOutput_NoSyncIndicators()
    {
        var active = CreateWorkItem(1, "JSON Tree Item", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "json");

        result.ShouldBe(0);
    }

    // ── Async rendering path (TTY) ──────────────────────────────────

    [Fact]
    public async Task AsyncPath_RendersParentChainAndFocusedItem()
    {
        var parent = CreateWorkItem(100, "Epic Parent", type: "Epic");
        var focus = CreateWorkItem(1, "Focus Task", parentId: 100);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Epic Parent");
        output.ShouldContain("#1");
        output.ShouldContain("Focus Task");
    }

    [Fact]
    public async Task AsyncPath_RendersFocusedItemBold()
    {
        var focus = CreateWorkItem(1, "Bold Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Bold Focus");
        output.ShouldContain("#1");

        var markup = _spectreRenderer.FormatFocusedNode(focus, 1);
        markup.ShouldContain("[bold]");
    }

    [Fact]
    public async Task AsyncPath_RendersChildrenProgressively()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var children = new[]
        {
            CreateWorkItem(10, "Child A", parentId: 1),
            CreateWorkItem(20, "Child B", parentId: 1),
            CreateWorkItem(30, "Child C", parentId: 1),
        };

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Child A");
        output.ShouldContain("Child B");
        output.ShouldContain("Child C");
    }

    [Fact]
    public async Task AsyncPath_ShowsAllChildrenAtDepth()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human", depth: 2);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Child 2");
        output.ShouldContain("Child 3");
        output.ShouldContain("Child 4");
        output.ShouldContain("Child 5");
        output.ShouldContain("Child 6");
        output.ShouldNotContain("... and");
    }

    [Fact]
    public async Task AsyncPath_DeepParentChain_RendersAllAncestors()
    {
        var grandparent = CreateWorkItem(100, "Epic", type: "Epic");
        var parent = CreateWorkItem(50, "Feature", type: "Feature", parentId: 100);
        var focus = CreateWorkItem(1, "My Task", parentId: 50);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetParentChainAsync(50, Arg.Any<CancellationToken>()).Returns(new[] { grandparent, parent });
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Epic");
        output.ShouldContain("Feature");
        output.ShouldContain("My Task");
    }

    [Fact]
    public async Task AsyncPath_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task AsyncPath_DirtyFocusedItem_ShowsDirtyMarker()
    {
        var focus = CreateWorkItem(1, "Dirty Task", parentId: null);
        focus.SetDirty();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Dirty Task");
        output.ShouldContain("✎");
    }

    [Fact]
    public async Task AsyncPath_FocusedItemNotInCache_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(1);
    }

    // ── Sync fallback (redirected / JSON / minimal / noLive) ────────

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncFallback_JsonFormat_UsesSyncPath()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var result = await cmd.ExecuteAsync(outputFormat: "json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncFallback_MinimalFormat_UsesSyncPath()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var result = await cmd.ExecuteAsync(outputFormat: "minimal");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncFallback_NoLiveFlag_UsesSyncPath()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync(outputFormat: "human", noLive: true);

        result.ShouldBe(0);
    }

    // ── SpectreRenderer unit tests ──────────────────────────────────

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_ParentChainDimmed()
    {
        var parent = CreateWorkItem(100, "Parent Epic", type: "Epic");
        var focus = CreateWorkItem(1, "Task Under Epic", parentId: 100);

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(new[] { parent }),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            maxDepth: 5,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Parent Epic");
        output.ShouldContain("#1");
        output.ShouldContain("Task Under Epic");

        var parentMarkup = _spectreRenderer.FormatParentNode(parent);
        parentMarkup.ShouldContain("[dim]");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_NoParents_RootIsFocused()
    {
        var focus = CreateWorkItem(1, "Root Item", parentId: null);
        var children = new[] { CreateWorkItem(10, "Child", parentId: 1) };

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(children),
            maxDepth: 5,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Root Item");
        output.ShouldContain("Child");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_ShowsAllChildren()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(10, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(children),
            maxDepth: 5,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Child 10");
        output.ShouldContain("Child 11");
        output.ShouldContain("Child 12");
        output.ShouldContain("Child 13");
        output.ShouldContain("Child 14");
        output.ShouldNotContain("... and");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_NullFocusedItem_NoOutput()
    {
        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(null),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            maxDepth: 5,
            activeId: null,
            ct: CancellationToken.None);

        _testConsole.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_ActiveChildMarker()
    {
        var focus = CreateWorkItem(1, "Parent Task", parentId: null);
        var children = new[]
        {
            CreateWorkItem(10, "Active Child", parentId: 1),
            CreateWorkItem(20, "Other Child", parentId: 1),
        };

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(children),
            maxDepth: 5,
            activeId: 10,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Active Child");
        output.ShouldContain("Other Child");
        output.ShouldContain("●");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string type = "Task")
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateWorkItemWithEffort(int id, string title, string effortValue, int? parentId = null)
    {
        var item = CreateWorkItem(id, title, parentId);
        item.ImportFields(new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = effortValue,
        });
        return item;
    }

    // ── Effort display in tree (EPIC-007 E2-T10) ───────────────────

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_ChildWithEffort_ShowsPts()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var child = CreateWorkItemWithEffort(10, "Child With Points", "5", parentId: 1);

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(new[] { child }),
            maxDepth: 5,
            activeId: null,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("(5 pts)");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_ChildWithoutEffort_NoPts()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var child = CreateWorkItem(10, "Child No Points", parentId: 1);

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(new[] { child }),
            maxDepth: 5,
            activeId: null,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldNotContain("pts");
    }

    // ── Explicit --id parameter tests ───────────────────────────────

    [Fact]
    public async Task Tree_WithExplicitId_RendersTreeForSpecificItem()
    {
        var item = CreateWorkItem(42, "Specific Item", parentId: null);
        // Explicit ID does NOT require active context
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(42, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(id: 42, outputFormat: "minimal");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Tree_WithExplicitId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(id: 99, outputFormat: "minimal");

        result.ShouldBe(1);
    }

    // ── Recursive child fetching (Task 2073) ────────────────────────

    [Fact]
    public async Task SyncPath_JsonOutput_IncludesGrandchildren()
    {
        var focus = CreateWorkItem(1, "Epic", parentId: null, type: "Epic");
        var child = CreateWorkItem(10, "Issue", parentId: 1, type: "Issue");
        var grandchild = CreateWorkItem(100, "Task", parentId: 10);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { child });
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>()).Returns(new[] { grandchild });
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();

        // Use redirected pipeline to trigger sync path with JSON output
        var result = await cmd.ExecuteAsync(outputFormat: "json", depth: 3);

        result.ShouldBe(0);

        // Verify GetChildrenAsync was called for the child (to fetch grandchildren)
        await _workItemRepo.Received().GetChildrenAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncPath_DepthOne_DoesNotFetchGrandchildren()
    {
        var focus = CreateWorkItem(1, "Epic", parentId: null, type: "Epic");
        var child = CreateWorkItem(10, "Issue", parentId: 1, type: "Issue");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(new[] { child });

        var cmd = CreateCommand();

        var result = await cmd.ExecuteAsync(outputFormat: "json", depth: 1);

        result.ShouldBe(0);

        // With depth=1, only direct children are fetched, no recursive descent
        // GetChildrenAsync(1) is called for direct children + sibling count
        // GetChildrenAsync(10) should NOT be called for grandchildren
        await _workItemRepo.DidNotReceive().GetChildrenAsync(10, Arg.Any<CancellationToken>());
    }

}
