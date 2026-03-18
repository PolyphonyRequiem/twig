using NSubstitute;
using Shouldly;
using Spectre.Console;
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

public class TreeCommandAsyncTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly TwigConfiguration _config;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly OutputFormatterFactory _formatterFactory;

    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;

    public TreeCommandAsyncTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _config = new TwigConfiguration();

        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, 30);
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
    }

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a TTY environment.
    /// </summary>
    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a redirected/piped environment.
    /// </summary>
    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private TreeCommand CreateCommand(RenderingPipelineFactory pipelineFactory) =>
        new(_contextStore, _workItemRepo, _config, _formatterFactory, _activeItemResolver,
            _workingSetService, _syncCoordinator, pipelineFactory);

    // ── Async rendering path tests ──────────────────────────────────

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
        var result = await cmd.ExecuteAsync("human");

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
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Bold Focus");
        output.ShouldContain("#1");

        // Verify the focused node markup contains [bold] via the internal format method
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
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Child A");
        output.ShouldContain("Child B");
        output.ShouldContain("Child C");
    }

    [Fact]
    public async Task AsyncPath_RespectsMaxChildren()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(2, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(children);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", depth: 2);

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Child 2");
        output.ShouldContain("Child 3");
        output.ShouldContain("... and 3 more");
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
        var result = await cmd.ExecuteAsync("human");

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

        var originalErr = Console.Error;
        using var errWriter = new StringWriter();
        Console.SetError(errWriter);
        int result;
        try
        {
            result = await cmd.ExecuteAsync("human");
        }
        finally
        {
            Console.SetError(originalErr);
        }

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("No active work item");
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
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Dirty Task");
        output.ShouldContain("•");
    }

    [Fact]
    public async Task AsyncPath_FocusedItemNotInCache_ReturnsError()
    {
        // activeId exists in context store but GetByIdAsync returns null (cache miss)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var originalErr = Console.Error;
        using var errWriter = new StringWriter();
        Console.SetError(errWriter);
        int result;
        try
        {
            result = await cmd.ExecuteAsync("human");
        }
        finally
        {
            Console.SetError(originalErr);
        }

        result.ShouldBe(1);
        errWriter.ToString().ShouldContain("#42");
        errWriter.ToString().ShouldContain("not found in cache");
    }

    // ── Sync fallback tests ─────────────────────────────────────────

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

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

        var originalOut = Console.Out;
        using var outWriter = new StringWriter();
        Console.SetOut(outWriter);
        try
        {
            await cmd.ExecuteAsync("json");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        // JSON output should be produced (not Spectre rendering)
        outWriter.ToString().ShouldContain("\"id\":");
    }

    [Fact]
    public async Task SyncFallback_MinimalFormat_UsesSyncPath()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        var originalOut = Console.Out;
        using var outWriter = new StringWriter();
        Console.SetOut(outWriter);
        try
        {
            await cmd.ExecuteAsync("minimal");
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        outWriter.ToString().ShouldContain("#1");
    }

    [Fact]
    public async Task SyncFallback_NoLiveFlag_UsesSyncPath()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(focus);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true);

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
            maxChildren: 10,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Parent Epic");
        output.ShouldContain("#1");
        output.ShouldContain("Task Under Epic");

        // Verify parent node markup contains [dim] via the internal format method
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
            maxChildren: 10,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Root Item");
        output.ShouldContain("Child");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_TruncatesChildren()
    {
        var focus = CreateWorkItem(1, "Focus", parentId: null);
        var children = Enumerable.Range(10, 5)
            .Select(i => CreateWorkItem(i, $"Child {i}", parentId: 1))
            .ToArray();

        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(focus),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(children),
            maxChildren: 2,
            activeId: 1,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Child 10");
        output.ShouldContain("Child 11");
        output.ShouldContain("... and 3 more");
        output.ShouldNotContain("Child 12");
    }

    [Fact]
    public async Task SpectreRenderer_RenderTreeAsync_NullFocusedItem_NoOutput()
    {
        await _spectreRenderer.RenderTreeAsync(
            getFocusedItem: () => Task.FromResult<WorkItem?>(null),
            getParentChain: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            getChildren: () => Task.FromResult<IReadOnlyList<WorkItem>>(Array.Empty<WorkItem>()),
            maxChildren: 10,
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
            maxChildren: 10,
            activeId: 10,
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Active Child");
        output.ShouldContain("Other Child");
        // The active child should have the ● marker
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

}
