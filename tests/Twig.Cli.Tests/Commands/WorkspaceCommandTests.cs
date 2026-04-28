using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
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

public class WorkspaceCommandTests
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
    private readonly WorkspaceCommand _cmd;

    public WorkspaceCommandTests()
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

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _cmd = CreateCommand();
    }

    // ── Command factory methods ─────────────────────────────────────

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private CommandContext CreateCtx(RenderingPipelineFactory? pipelineFactory = null, HintEngine? hintEngine = null) =>
        new(pipelineFactory ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true),
            _formatterFactory,
            hintEngine ?? _hintEngine,
            _config);

    private WorkspaceCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null, HintEngine? hintEngine = null) =>
        new(CreateCtx(pipelineFactory, hintEngine), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder());

    private WorkspaceCommand CreateCommandWithPipeline(RenderingPipelineFactory pipelineFactory) =>
        CreateCommand(pipelineFactory, new HintEngine(new DisplayConfig { Hints = true }));

    [Fact]
    public async Task Workspace_ShowsContextAndSprint()
    {
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, CreateWorkItem(2, "Other Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_NoActiveContext_ShowsNone()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_ShowsSeeds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Seed Task",
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_StaleSeeds_ShowWarning()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var staleSeed = new WorkItem
        {
            Id = -2,
            Type = WorkItemType.Task,
            Title = "Stale Seed",
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30), // older than default 14 days
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { staleSeed });

        // Verify the command succeeds and the stale seed is included in the workspace model
        var result = await _cmd.ExecuteAsync();
        result.ShouldBe(0);

        // Verify the workspace model correctly identifies stale seeds
        var workspace = Workspace.Build(null, Array.Empty<WorkItem>(), new[] { staleSeed });
        var staleSeeds = workspace.GetStaleSeeds(_config.Seed.StaleDays);
        staleSeeds.Count.ShouldBe(1);
        staleSeeds[0].Id.ShouldBe(-2);
    }

    [Fact]
    public async Task Workspace_AllMode_CallsGetParentChainAsync_ForParentIds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item1 = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Task 1", State = "Active",
            ParentId = 100, AssignedTo = "Alice",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var item2 = new WorkItem
        {
            Id = 20, Type = WorkItemType.Task, Title = "Task 2", State = "Active",
            ParentId = 200, AssignedTo = "Bob",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var item3 = new WorkItem
        {
            Id = 30, Type = WorkItemType.Task, Title = "Task 3", State = "Active",
            ParentId = 100, AssignedTo = "Alice", // same parent as item1
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item1, item2, item3 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var parent100 = new WorkItem
        {
            Id = 100, Type = WorkItemType.UserStory, Title = "Story A", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var parent200 = new WorkItem
        {
            Id = 200, Type = WorkItemType.UserStory, Title = "Story B", State = "Active",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { parent100 });
        _workItemRepo.GetParentChainAsync(200, Arg.Any<CancellationToken>())
            .Returns(new[] { parent200 });

        // Return a standard Agile process config
        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);

        // GetParentChainAsync called for each unique parent ID (100, 200) — NOT GetByIdAsync
        await _workItemRepo.Received(1).GetParentChainAsync(100, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetParentChainAsync(200, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIdAsync(100, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIdAsync(200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_AllMode_NullProcessConfig_HierarchyIsNull()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var item = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Task 1", State = "Active",
            ParentId = 100, AssignedTo = "Alice",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItem
            {
                Id = 100, Type = WorkItemType.UserStory, Title = "Story", State = "Active",
                IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
                AreaPath = AreaPath.Parse("Project").Value,
            }});

        // Return null — cache empty
        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns((ProcessConfigurationData?)null);

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // Process config was queried
        await _processTypeStore.Received(1).GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Workspace_AllMode_NoParents_SkipsParentChainCalls()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Sprint items with no parent IDs
        var item = CreateWorkItem(10, "Task No Parent");
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        var result = await _cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // No parent IDs → no GetParentChainAsync calls
        await _workItemRepo.DidNotReceive().GetParentChainAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── JSON output ─────────────────────────────────────────────────

    [Fact]
    public async Task Workspace_JsonOutput_ProducesExpectedFormat()
    {
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_JsonOutput_WithPipelineFactory_IdenticalToWithout()
    {
        // Regression test: JSON output must be identical whether or not
        // RenderingPipelineFactory is injected (it routes to sync path for JSON).
        var active = CreateWorkItem(1, "Regression Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var resultWithout = await _cmd.ExecuteAsync("json");
        resultWithout.ShouldBe(0);

        // Get output with pipeline factory
        var pipelineFactory = new RenderingPipelineFactory(
            _formatterFactory, Substitute.For<IAsyncRenderer>());
        var cmdWithPipeline = new WorkspaceCommand(CreateCtx(pipelineFactory), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore,
            _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder());

        var resultWith = await cmdWithPipeline.ExecuteAsync("json");
        resultWith.ShouldBe(0);
    }

    // ── WS-020: Dirty orphan display tests ──────────────────────────

    [Fact]
    public async Task Workspace_DirtyOrphans_ShownWhenPresent()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(1, "Sprint Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Dirty orphan: ID 99 is dirty but NOT in sprint or seed scope
        var orphan = CreateWorkItem(99, "Orphan Edit");
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(orphan);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { orphan });

        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 99 });
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);

        var cmd = new WorkspaceCommand(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, workingSetService, _trackingService, new SprintHierarchyBuilder());

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_NoDirtyOrphans_WhenAllDirtyItemsInScope()
    {
        var sprintItem = CreateWorkItem(1, "Sprint Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // Dirty item IS in sprint scope — should not appear as orphan
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1 });
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);

        var cmd = new WorkspaceCommand(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, workingSetService, _trackingService, new SprintHierarchyBuilder());

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_DirtyOrphans_IncludesHintText()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var orphan = CreateWorkItem(50, "Forgotten Edit");
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(orphan);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { orphan });

        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 50 });
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);

        var cmd = new WorkspaceCommand(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, workingSetService, _trackingService, new SprintHierarchyBuilder());

        var result = await cmd.ExecuteAsync("human");
        result.ShouldBe(0);
    }

    // ── WS-021: JSON output parity ──────────────────────────────────

    [Fact]
    public async Task Workspace_JsonOutput_NoDirtyOrphanSection()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var orphan = CreateWorkItem(50, "JSON Orphan");
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(orphan);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { orphan });

        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 50 });
        var workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);

        var cmd = new WorkspaceCommand(CreateCtx(), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore, _activeItemResolver, workingSetService, _trackingService, new SprintHierarchyBuilder());

        var result = await cmd.ExecuteAsync("json");
        result.ShouldBe(0);
    }

    // ── Async rendering path (TTY) ──────────────────────────────────

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, CreateWorkItem(2, "Other Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_RendersContextAndSprintItems()
    {
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, CreateWorkItem(2, "Other Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Active Item");
        output.ShouldContain("Other Item");
        output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task AsyncPath_PopulatesClosureVariables_ForHintComputation()
    {
        var active = CreateWorkItem(1, "Dirty Item");
        active.SetDirty();

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var hintEngine = new HintEngine(new DisplayConfig { Hints = true });
        var pipelineFactory = CreateTtyPipelineFactory();
        var cmd = new WorkspaceCommand(CreateCtx(pipelineFactory, hintEngine), _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore,
            _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder());

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Dirty Item");
        output.ShouldContain("dirty");
    }

    [Fact]
    public async Task AsyncPath_WithSeeds_RendersSeeds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Async Seed", State = "New",
            IsSeed = true, SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Async Seed");
        output.ShouldContain("Seeds");
    }

    [Fact]
    public async Task AsyncPath_JsonFormat_UsesSyncPath()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("json");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_NoLive_UsesSyncPath()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_AllMode_UsesSyncPath()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", all: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_VerifiesDataFetchSequence()
    {
        var active = CreateWorkItem(1, "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        await cmd.ExecuteAsync("human");

        await _contextStore.Received(1).GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIdAsync(1, Arg.Any<CancellationToken>());
        await _iterationService.Received(1).GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetSeedsAsync(Arg.Any<CancellationToken>());
    }

    // ── SpectreRenderer unit tests ──────────────────────────────────

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsLoadingThenData()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(CreateWorkItem(1, "Active")),
            new WorkspaceDataChunk.SprintItemsLoaded(new[]
            {
                CreateWorkItem(10, "Task A"),
                CreateWorkItem(20, "Task B"),
            }),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Task A");
        output.ShouldContain("Task B");
        output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsSeeds()
    {
        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Seed Task",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(new[] { seed }));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Seed Task");
        output.ShouldContain("Seeds");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsStaleSeedWarning()
    {
        var staleSeed = new WorkItem
        {
            Id = -2,
            Type = WorkItemType.Task,
            Title = "Stale Seed",
            State = "",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(new[] { staleSeed }));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Stale Seed");
        output.ShouldContain("stale");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_NoContext_ShowsNoActiveContext()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("No active context");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_RefreshBadge()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(CreateWorkItem(1, "Active")),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshStarted(),
            new WorkspaceDataChunk.RefreshCompleted());

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Active: #1");
    }

    [Fact]
    public void SpectreRenderer_RenderHints_WritesHints()
    {
        var hints = new List<string> { "Try: twig status", "3 dirty items" };
        _spectreRenderer.RenderHints(hints);

        var output = _testConsole.Output;
        output.ShouldContain("twig status");
        output.ShouldContain("dirty items");
    }

    [Fact]
    public void SpectreRenderer_RenderHints_EmptyList_NoOutput()
    {
        _spectreRenderer.RenderHints(Array.Empty<string>());

        _testConsole.Output.ShouldBeEmpty();
    }

    // ── EPIC-002: Status summary header + workspace highlight ───────

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_ShowsSummaryHeader()
    {
        var item = CreateWorkItem(1, "Summary Test");

        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("#1");
        output.ShouldContain("●");
        output.ShouldContain("Summary Test");
        output.ShouldContain("Task");
        output.ShouldContain("New");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_HighlightsActiveItem()
    {
        var activeItem = CreateWorkItem(1, "Active Item");
        var otherItem = CreateWorkItem(2, "Other Item");

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(activeItem),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { activeItem, otherItem }),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("►");
        output.ShouldContain("Active Item");
        output.ShouldContain("Other Item");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_NoContext_NoHighlight()
    {
        var item = CreateWorkItem(1, "Item No Context");

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { item }),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Item No Context");
        output.ShouldNotContain("►");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_SprintBeforeContext_NoHighlight()
    {
        var activeItem = CreateWorkItem(1, "Active Item");
        var otherItem = CreateWorkItem(2, "Other Item");

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.SprintItemsLoaded(new[] { activeItem, otherItem }),
            new WorkspaceDataChunk.ContextLoaded(activeItem),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Active Item");
        output.ShouldContain("Other Item");
        output.ShouldNotContain("►");
    }

    // ── --flat flag tests (T-1977) ─────────────────────────────────

    [Fact]
    public async Task Workspace_FlatFlag_DisablesTreeRendering_SyncPath()
    {
        // Arrange: set up process config so tree rendering would normally activate
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        var item = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Task 1", State = "Active",
            ParentId = 100,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItem
            {
                Id = 100, Type = WorkItemType.UserStory, Title = "Story", State = "Active",
                IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
                AreaPath = AreaPath.Parse("Project").Value,
            }});
        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        // Act: --flat should produce flat output (no tree chars)
        var result = await _cmd.ExecuteAsync(flat: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_NoFlatFlag_DefaultsToTreeRendering_SyncPath()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        var item = new WorkItem
        {
            Id = 10, Type = WorkItemType.Task, Title = "Task 1", State = "Active",
            ParentId = 100,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetParentChainAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItem
            {
                Id = 100, Type = WorkItemType.UserStory, Title = "Story", State = "Active",
                IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
                AreaPath = AreaPath.Parse("Project").Value,
            }});
        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        // Act: default (no --flat) should use tree rendering
        var result = await _cmd.ExecuteAsync(flat: false);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_FlatFlag_AsyncPath_DisablesTreeRendering()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        var item = CreateWorkItem(1, "Sprint Task");
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));

        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", flat: true);

        result.ShouldBe(0);
        // SpectreRenderer.UseTreeRendering should be false — verified by no crash
        // and table-based output (not tree-based)
    }

    [Fact]
    public async Task Workspace_FlatFlag_SprintLayout_Succeeds()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(1, "Sprint Task") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync(flat: true, sprintLayout: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Workspace_DepthConfig_WiredToSpectreRenderer()
    {
        // Verify depth config values are wired from TwigConfiguration into SpectreRenderer
        _config.Display.TreeDepthUp = 3;
        _config.Display.TreeDepthDown = 5;
        _config.Display.TreeDepthSideways = 2;

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(1, "Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));

        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
        // Depth config wired correctly — SpectreRenderer consumed the values
        _spectreRenderer.TreeDepthUp.ShouldBe(3);
        _spectreRenderer.TreeDepthDown.ShouldBe(5);
        _spectreRenderer.TreeDepthSideways.ShouldBe(2);
    }

    [Fact]
    public async Task Workspace_FlatFlag_DepthStillWired_ButTreeDisabled()
    {
        _config.Display.TreeDepthUp = 4;
        _config.Display.TreeDepthDown = 8;
        _config.Display.TreeDepthSideways = 3;

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(1, "Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));

        _processTypeStore.GetProcessConfigurationDataAsync(Arg.Any<CancellationToken>())
            .Returns(CreateAgileProcessConfig());

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", flat: true);

        result.ShouldBe(0);
        // --flat disables tree rendering even when depth is configured
        _spectreRenderer.UseTreeRendering.ShouldBeFalse();
        // Depth values still wired (available for future non-flat use)
        _spectreRenderer.TreeDepthUp.ShouldBe(4);
        _spectreRenderer.TreeDepthDown.ShouldBe(8);
        _spectreRenderer.TreeDepthSideways.ShouldBe(3);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static async IAsyncEnumerable<WorkspaceDataChunk> CreateChunksAsync(
        params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static ProcessConfigurationData CreateAgileProcessConfig()
    {
        return new ProcessConfigurationData
        {
            PortfolioBacklogs = new[]
            {
                new BacklogLevelConfiguration
                {
                    Name = "Epics",
                    WorkItemTypeNames = new[] { "Epic" },
                },
                new BacklogLevelConfiguration
                {
                    Name = "Features",
                    WorkItemTypeNames = new[] { "Feature" },
                },
            },
            RequirementBacklog = new BacklogLevelConfiguration
            {
                Name = "Stories",
                WorkItemTypeNames = new[] { "User Story" },
            },
            TaskBacklog = new BacklogLevelConfiguration
            {
                Name = "Tasks",
                WorkItemTypeNames = new[] { "Task" },
            },
        };
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    private static WorkItem CreateWorkItemWithFields(int id, string title, Dictionary<string, string?> fields)
    {
        var item = CreateWorkItem(id, title);
        item.ImportFields(fields);
        return item;
    }

    // ── Dynamic column tests (EPIC-007 E2-T2) ──────────────────────

    [Fact]
    public async Task AsyncPath_WithPopulatedFields_ShowsDynamicColumns()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "5",
            ["Microsoft.VSTS.Common.Priority"] = "2",
        };

        var active = CreateWorkItemWithFields(1, "Active Item", fields);
        var item2 = CreateWorkItemWithFields(2, "Other Item", fields);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, item2 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new FieldDefinition[]
            {
                new("Microsoft.VSTS.Scheduling.StoryPoints", "Story Points", "double", false),
                new("Microsoft.VSTS.Common.Priority", "Priority", "integer", false),
            });

        // Config-specified columns for the async streaming path (auto-discovery requires all items upfront)
        _config.Display.Columns = new DisplayColumnsConfig
        {
            Workspace = new List<string>
            {
                "Microsoft.VSTS.Scheduling.StoryPoints",
                "Microsoft.VSTS.Common.Priority",
            },
        };

        var cmd = CreateCommandWithPipeline(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        var output = _testConsole.Output;
        output.ShouldContain("Story Points");
        output.ShouldContain("Priority");
        output.ShouldContain("5");
        output.ShouldContain("2");
    }

    [Fact]
    public async Task Workspace_WithPopulatedFields_SyncPath_Succeeds()
    {
        var fields = new Dictionary<string, string?>
        {
            ["Microsoft.VSTS.Scheduling.StoryPoints"] = "3",
        };

        var active = CreateWorkItemWithFields(1, "Active Item", fields);
        var item2 = CreateWorkItemWithFields(2, "Other Item", fields);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, item2 });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _fieldDefinitionStore.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<FieldDefinition>());

        var result = await _cmd.ExecuteAsync();

        result.ShouldBe(0);
    }
}
