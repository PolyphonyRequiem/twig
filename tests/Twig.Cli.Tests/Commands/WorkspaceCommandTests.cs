using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.Services;
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
    private readonly IAdoWorkItemService _adoService;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly WorkingSetService _workingSetService;
    private readonly WorkspaceCommand _cmd;

    public WorkspaceCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _config = new TwigConfiguration();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, pendingChangeStore, _iterationService, null);

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            formatterFactory, hintEngine, _processTypeStore, _activeItemResolver, _workingSetService);
    }

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

        // Capture stdout
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await _cmd.ExecuteAsync("json");
            result.ShouldBe(0);

            var json = sw.ToString().Trim();
            // JSON must contain the canonical fields
            json.ShouldContain("\"context\":");
            json.ShouldContain("\"sprintItems\":");
            json.ShouldContain("\"seeds\":");
            json.ShouldContain("\"staleSeeds\":");
            json.ShouldContain("\"dirtyCount\":");
            json.ShouldContain("\"id\": 1");
            json.ShouldContain("\"title\": \"Active Item\"");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
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

        // Get output without pipeline factory (existing path)
        var sw1 = new StringWriter();
        Console.SetOut(sw1);
        string jsonWithout;
        try
        {
            await _cmd.ExecuteAsync("json");
            jsonWithout = sw1.ToString().Trim();
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        // Get output with pipeline factory
        var pipelineFactory = new RenderingPipelineFactory(
            new OutputFormatterFactory(new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter()),
            NSubstitute.Substitute.For<IAsyncRenderer>());
        var cmdWithPipeline = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            new OutputFormatterFactory(new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter()),
            new HintEngine(new DisplayConfig { Hints = false }), _processTypeStore,
            _activeItemResolver, _workingSetService, pipelineFactory);

        var sw2 = new StringWriter();
        Console.SetOut(sw2);
        string jsonWith;
        try
        {
            await cmdWithPipeline.ExecuteAsync("json");
            jsonWith = sw2.ToString().Trim();
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }

        jsonWith.ShouldBe(jsonWithout);
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

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            formatterFactory, hintEngine, _processTypeStore, _activeItemResolver, workingSetService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("Unsaved changes");
            output.ShouldContain("Orphan Edit");
            output.ShouldContain("twig save");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
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

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            formatterFactory, hintEngine, _processTypeStore, _activeItemResolver, workingSetService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldNotContain("Unsaved changes");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
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

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            formatterFactory, hintEngine, _processTypeStore, _activeItemResolver, workingSetService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("human");
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("Run 'twig save' to push these changes.");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
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

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            formatterFactory, hintEngine, _processTypeStore, _activeItemResolver, workingSetService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync("json");
            result.ShouldBe(0);
            var output = sw.ToString();
            // JSON output must NOT contain dirty orphan section
            output.ShouldNotContain("Unsaved changes");
            output.ShouldNotContain("twig save");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }
}
