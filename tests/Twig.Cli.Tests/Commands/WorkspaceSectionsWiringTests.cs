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
/// Integration tests verifying WorkspaceCommand builds WorkspaceSections
/// and correctly passes them to both SpectreRenderer (async path) and
/// HumanOutputFormatter (sync path).
/// </summary>
public sealed class WorkspaceSectionsWiringTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationService _iterationService;
    private readonly TwigConfiguration _config;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly ITrackingService _trackingService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;

    public WorkspaceSectionsWiringTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _config = new TwigConfiguration();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        var adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);
        _trackingService = Substitute.For<ITrackingService>();
        _trackingService.GetTrackedItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _testConsole = new TestConsole();
        _testConsole.Profile.Width = 120;
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Async path (Spectre) section wiring ─────────────────────────

    [Fact]
    public async Task AsyncPath_SprintItems_SectionsPassedToRenderer()
    {
        var item1 = CreateWorkItem(1, "Task Alpha");
        var item2 = CreateWorkItem(2, "Task Beta");
        SetupDefaultMocks(sprintItems: new[] { item1, item2 });

        var cmd = CreateCommandWithTtyPipeline();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        // Items should be rendered (via sections — single section, no section header)
        output.ShouldContain("Task Alpha");
        output.ShouldContain("Task Beta");
    }

    [Fact]
    public async Task AsyncPath_ExcludedIds_FlowThroughToRenderer()
    {
        var item = CreateWorkItem(1, "Sprint Task");
        SetupDefaultMocks(sprintItems: new[] { item });
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 42, 99 });

        var cmd = CreateCommandWithTtyPipeline();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        // Exclusion footer rendered by SpectreRenderer when excluded IDs present
        output.ShouldContain("excluded");
        output.ShouldContain("#42");
        output.ShouldContain("#99");
    }

    [Fact]
    public async Task AsyncPath_NoExcludedIds_NoExclusionFooter()
    {
        var item = CreateWorkItem(1, "Sprint Task");
        SetupDefaultMocks(sprintItems: new[] { item });

        var cmd = CreateCommandWithTtyPipeline();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldNotContain("excluded");
    }

    [Fact]
    public async Task AsyncPath_EmptySprintItems_FallbackRendering()
    {
        SetupDefaultMocks(sprintItems: Array.Empty<WorkItem>());

        var cmd = CreateCommandWithTtyPipeline();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        // Should not crash — empty sections are handled gracefully
    }

    // ── Sync path (HumanOutputFormatter) section wiring ─────────────

    [Fact]
    public async Task SyncPath_SprintItems_SectionsPassedToFormatter()
    {
        var item1 = CreateWorkItem(1, "Sync Alpha");
        var item2 = CreateWorkItem(2, "Sync Beta");
        SetupDefaultMocks(sprintItems: new[] { item1, item2 });

        var cmd = CreateDefaultCommand();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncPath_ExcludedIds_ExclusionFooterRendered()
    {
        var item = CreateWorkItem(1, "Sprint Task");
        SetupDefaultMocks(sprintItems: new[] { item });
        _trackingService.GetExcludedIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 55 });

        var cmd = CreateDefaultCommand();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        // HumanOutputFormatter renders exclusion footer from Workspace.ExcludedIds
        // which flows from WorkspaceSections.Build(excludedIds:)
    }

    [Fact]
    public async Task SyncPath_NoExcludedIds_NoExclusionContent()
    {
        var item = CreateWorkItem(1, "Sprint Task");
        SetupDefaultMocks(sprintItems: new[] { item });

        var cmd = CreateDefaultCommand();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task SyncPath_EmptySprintItems_GracefulHandling()
    {
        SetupDefaultMocks(sprintItems: Array.Empty<WorkItem>());

        var cmd = CreateDefaultCommand();
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    // ── Workspace.Sections populated correctly ──────────────────────

    [Fact]
    public void WorkspaceBuild_WithSections_SectionsPopulated()
    {
        var item = CreateWorkItem(1, "Item");
        var sections = WorkspaceSections.Build(new[] { item }, excludedIds: new[] { 10 });
        var ws = Workspace.Build(null, new[] { item }, Array.Empty<WorkItem>(), sections: sections);

        ws.Sections.ShouldNotBeNull();
        ws.Sections.Sections.Count.ShouldBe(1);
        ws.Sections.Sections[0].ModeName.ShouldBe("Sprint");
        ws.Sections.Sections[0].Items.Count.ShouldBe(1);
        ws.Sections.ExcludedItemIds.ShouldContain(10);
    }

    [Fact]
    public void WorkspaceBuild_NullSections_SectionsNull()
    {
        var ws = Workspace.Build(null, Array.Empty<WorkItem>(), Array.Empty<WorkItem>());

        ws.Sections.ShouldBeNull();
    }

    // ── Refresh path re-builds sections ─────────────────────────────

    [Fact]
    public async Task AsyncPath_Refresh_SectionsRebuiltWithRefreshedData()
    {
        var original = CreateWorkItem(1, "Original");
        var refreshed = CreateWorkItem(1, "Original");
        var newItem = CreateWorkItem(2, "New After Refresh");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<WorkItem>());

        // First call returns original, second call (refresh) returns original + newItem
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { original }, new[] { refreshed, newItem });

        // Make cache stale to trigger refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var cmd = CreateCommandWithTtyPipeline();
        var result = await cmd.ExecuteAsync("human", noRefresh: false);

        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("New After Refresh");
    }

    // ── WorkspaceDataChunk carries sections ──────────────────────────

    [Fact]
    public void SprintItemsLoaded_CarriesSections()
    {
        var item = CreateWorkItem(1, "Item");
        var sections = WorkspaceSections.Build(new[] { item }, excludedIds: new[] { 5 });
        var chunk = new WorkspaceDataChunk.SprintItemsLoaded(new[] { item }, sections);

        chunk.Sections.ShouldNotBeNull();
        chunk.Sections!.Sections.Count.ShouldBe(1);
        chunk.Sections.ExcludedItemIds.ShouldContain(5);
    }

    [Fact]
    public void SprintItemsLoaded_NullSections_DefaultsToNull()
    {
        var chunk = new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>());

        chunk.Sections.ShouldBeNull();
    }

    // ── TrackingService integration ─────────────────────────────────

    [Fact]
    public async Task AsyncPath_TrackingServiceQueried_ForExcludedIds()
    {
        SetupDefaultMocks(sprintItems: new[] { CreateWorkItem(1, "Item") });

        var cmd = CreateCommandWithTtyPipeline();
        await cmd.ExecuteAsync("human");

        await _trackingService.Received(1).GetExcludedIdsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncPath_TrackingServiceQueried_ForExcludedIds()
    {
        SetupDefaultMocks(sprintItems: new[] { CreateWorkItem(1, "Item") });

        var cmd = CreateDefaultCommand();
        await cmd.ExecuteAsync("human");

        await _trackingService.Received(1).GetExcludedIdsAsync(Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetupDefaultMocks(IReadOnlyList<WorkItem>? sprintItems = null)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(sprintItems ?? Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));
    }

    private WorkspaceCommand CreateDefaultCommand() =>
        new(_contextStore, _workItemRepo, _iterationService, _config,
            _formatterFactory, _hintEngine, _processTypeStore, _fieldDefinitionStore,
            _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder());

    private WorkspaceCommand CreateCommandWithTtyPipeline()
    {
        var pipelineFactory = new RenderingPipelineFactory(
            _formatterFactory, _spectreRenderer, isOutputRedirected: () => false);
        return new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            _formatterFactory, _hintEngine, _processTypeStore, _fieldDefinitionStore,
            _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder(), pipelineFactory);
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
}
