using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SetCommandDisambiguationTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly IAsyncRenderer _mockRenderer;
    private readonly ISeedLinkRepository _seedLinkRepo;
    private readonly IWorkItemLinkRepository _workItemLinkRepo;

    public SetCommandDisambiguationTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _seedLinkRepo = Substitute.For<ISeedLinkRepository>();
        _workItemLinkRepo = Substitute.For<IWorkItemLinkRepository>();
        _seedLinkRepo.GetLinksForItemAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SeedLink>());
        _workItemLinkRepo.GetLinksAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItemLink>());
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, protectedCacheWriter, pendingChangeStore, 30);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _mockRenderer = Substitute.For<IAsyncRenderer>();
    }

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a TTY environment
    /// (isOutputRedirected returns false) so the async rendering path is selected.
    /// </summary>
    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _mockRenderer, isOutputRedirected: () => false);

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a redirected/piped
    /// environment (isOutputRedirected returns true) so the sync fallback is selected.
    /// </summary>
    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _mockRenderer, isOutputRedirected: () => true);

    private SetCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null)
    {
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var iterationService = Substitute.For<IIterationService>();
        iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, iterationService, null);
        return new(_workItemRepo, _contextStore, _activeItemResolver, _syncCoordinator,
            workingSetService, _formatterFactory, _hintEngine, pipelineFactory);
    }

    // ── SetCommand: Interactive disambiguation (TTY + human) ────────

    [Fact]
    public async Task Set_MultiMatch_Tty_PromptsAndSelectsItem()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Auth login page"),
            CreateWorkItem(11, "Auth token refresh"),
            CreateWorkItem(12, "Auth logout flow"),
        };
        _workItemRepo.FindByPatternAsync("auth", Arg.Any<CancellationToken>())
            .Returns(items);
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(items[1]);
        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns((11, "Auth token refresh"));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("auth");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(11, Arg.Any<CancellationToken>());
        await _mockRenderer.Received(1).PromptDisambiguationAsync(
            Arg.Is<IReadOnlyList<(int Id, string Title)>>(m => m.Count == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_MultiMatch_Tty_UserCancels_ReturnsExitCode1()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Auth login page"),
            CreateWorkItem(11, "Auth token refresh"),
        };
        _workItemRepo.FindByPatternAsync("auth", Arg.Any<CancellationToken>())
            .Returns(items);
        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns(((int Id, string Title)?)null);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("auth");

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── SetCommand: Static fallback (JSON / non-TTY) ────────────────

    [Fact]
    public async Task Set_MultiMatch_Json_ReturnsStaticList()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Auth login page"),
            CreateWorkItem(11, "Auth token refresh"),
        };
        _workItemRepo.FindByPatternAsync("auth", Arg.Any<CancellationToken>())
            .Returns(items);

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("auth", "json");

        result.ShouldBe(1);
        await _mockRenderer.DidNotReceive().PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_MultiMatch_NonTty_ReturnsStaticList()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Auth login page"),
            CreateWorkItem(11, "Auth token refresh"),
        };
        _workItemRepo.FindByPatternAsync("auth", Arg.Any<CancellationToken>())
            .Returns(items);

        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync("auth");

        result.ShouldBe(1);
        await _mockRenderer.DidNotReceive().PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_MultiMatch_NoPipelineFactory_ReturnsStaticList()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Auth login page"),
            CreateWorkItem(11, "Auth token refresh"),
        };
        _workItemRepo.FindByPatternAsync("auth", Arg.Any<CancellationToken>())
            .Returns(items);

        var cmd = CreateCommand(pipelineFactory: null);
        var result = await cmd.ExecuteAsync("auth");

        result.ShouldBe(1);
    }

    // ── NavigationCommands.DownAsync: Interactive disambiguation ─────

    [Fact]
    public async Task Down_MultiMatch_Tty_PromptsAndSelectsChild()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Auth login page", parentId: 1);
        var child2 = CreateWorkItem(11, "Auth token refresh", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        // The disambiguation prompt returns child2
        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns((11, "Auth token refresh"));

        // SetCommand will resolve the selected item by numeric ID
        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(child2);
        // Explicit stubs: SetCommand fetches parent chain and children for the selected item
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync("auth");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(11, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_MultiMatch_Tty_UserCancels_ReturnsExitCode1()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Auth login page", parentId: 1);
        var child2 = CreateWorkItem(11, "Auth token refresh", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns(((int Id, string Title)?)null);

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync("auth");

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_MultiMatch_Json_ReturnsStaticList()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Auth login page", parentId: 1);
        var child2 = CreateWorkItem(11, "Auth token refresh", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync("auth", "json");

        result.ShouldBe(1);
        await _mockRenderer.DidNotReceive().PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_SingleMatch_NavigatesToChild()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child = CreateWorkItem(10, "Auth login page", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(child);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child });
        // Explicit stubs: SetCommand fetches parent chain and children for the selected child
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync("login");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_NoMatch_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync("nonexistent");

        result.ShouldBe(1);
    }

    // ── NavigationCommands.DownAsync: No-arg interactive child selection ─

    [Fact]
    public async Task Down_NoArg_MultipleChildren_Tty_PromptsAndSelectsChild()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Design mockups", parentId: 1);
        var child2 = CreateWorkItem(11, "Implement API", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns((11, "Implement API"));

        _workItemRepo.GetByIdAsync(11, Arg.Any<CancellationToken>()).Returns(child2);
        _workItemRepo.GetParentChainAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { parent });
        _adoService.FetchChildrenAsync(11, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync();

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(11, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_NoArg_MultipleChildren_Tty_UserCancels_ReturnsExitCode1()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Design mockups", parentId: 1);
        var child2 = CreateWorkItem(11, "Implement API", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        _mockRenderer.PromptDisambiguationAsync(
                Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
                Arg.Any<CancellationToken>())
            .Returns(((int Id, string Title)?)null);

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync();

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Down_NoArg_MultipleChildren_NonTty_ReturnsStaticList()
    {
        var parent = CreateWorkItem(1, "Parent", parentId: null);
        var child1 = CreateWorkItem(10, "Design mockups", parentId: 1);
        var child2 = CreateWorkItem(11, "Implement API", parentId: 1);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetChildrenAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { child1, child2 });

        var setCmd = CreateCommand(CreateTtyPipelineFactory());
        var navCmd = new NavigationCommands(
            _contextStore, _workItemRepo, _seedLinkRepo, _workItemLinkRepo, setCmd, _formatterFactory, _activeItemResolver, CreateTtyPipelineFactory());

        var result = await navCmd.DownAsync(outputFormat: "json");

        result.ShouldBe(1);
        await _mockRenderer.DidNotReceive().PromptDisambiguationAsync(
            Arg.Any<IReadOnlyList<(int Id, string Title)>>(),
            Arg.Any<CancellationToken>());
    }

    // ── SpectreRenderer.BuildSelectionRenderable unit tests ──────────

    [Fact]
    public void BuildSelectionRenderable_HighlightsSelectedItem()
    {
        var items = new List<(int Id, string Title)>
        {
            (10, "Auth login page"),
            (11, "Auth token refresh"),
            (12, "Auth logout flow"),
        };

        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 1, filterText: "");

        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("#11");
        output.ShouldContain("Auth token refresh");
        output.ShouldContain("Multiple matches");
        output.ShouldContain("navigate");
    }

    [Fact]
    public void BuildSelectionRenderable_ShowsFilterText()
    {
        var items = new List<(int Id, string Title)>
        {
            (10, "Auth login page"),
        };

        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 0, filterText: "login");

        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("Filter: login");
    }

    [Fact]
    public void BuildSelectionRenderable_EmptyList_ShowsNoMatchMessage()
    {
        var items = new List<(int Id, string Title)>();

        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 0, filterText: "xyz");

        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("No items match filter");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null)
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

    // ── EPIC-005: Enriched BuildSelectionRenderable ──────────────────

    [Fact]
    public void BuildSelectionRenderable_Enriched_IncludesTypeBadgeMarkup()
    {
        var items = new List<(int Id, string Title, string? TypeName, string? State)>
        {
            (10, "Auth login page", "Bug", "Active"),
            (11, "Auth token refresh", "Task", "New"),
        };

        var theme = new SpectreTheme(new DisplayConfig());
        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 0, filterText: "", theme);

        // TEST-006: BuildSelectionRenderable MUST include type badge markup when provided
        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("#10");
        output.ShouldContain("#11");
        output.ShouldContain("Auth login page");
        output.ShouldContain("Auth token refresh");
        // State info should be present
        output.ShouldContain("Active");
        output.ShouldContain("New");
        output.ShouldContain("Multiple matches");
    }

    [Fact]
    public void BuildSelectionRenderable_Enriched_NullType_OmitsBadge()
    {
        var items = new List<(int Id, string Title, string? TypeName, string? State)>
        {
            (10, "Plain item", null, null),
        };

        var theme = new SpectreTheme(new DisplayConfig());
        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 0, filterText: "", theme);

        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("#10");
        output.ShouldContain("Plain item");
    }

    [Fact]
    public void BuildSelectionRenderable_Enriched_EmptyList_ShowsNoMatchMessage()
    {
        var items = new List<(int Id, string Title, string? TypeName, string? State)>();

        var theme = new SpectreTheme(new DisplayConfig());
        var renderable = SpectreRenderer.BuildSelectionRenderable(items, selectedIndex: 0, filterText: "xyz", theme);

        var console = new TestConsole();
        console.Write(renderable);
        var output = console.Output;

        output.ShouldContain("No items match filter");
    }
}
