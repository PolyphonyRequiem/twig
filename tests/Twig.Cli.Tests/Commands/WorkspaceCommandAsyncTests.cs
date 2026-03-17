using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class WorkspaceCommandAsyncTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationService _iterationService;
    private readonly TwigConfiguration _config;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public WorkspaceCommandAsyncTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _config = new TwigConfiguration();
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = true });
    }

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a TTY environment
    /// (isOutputRedirected returns false) so the async rendering path is selected.
    /// </summary>
    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    /// <summary>
    /// Creates a <see cref="RenderingPipelineFactory"/> that simulates a redirected/piped
    /// environment (isOutputRedirected returns true) so the sync fallback is selected.
    /// </summary>
    private RenderingPipelineFactory CreateRedirectedPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => true);

    private WorkspaceCommand CreateCommand(RenderingPipelineFactory pipelineFactory) =>
        new(_contextStore, _workItemRepo, _iterationService, _config,
            _formatterFactory, _hintEngine, _processTypeStore, pipelineFactory);

    [Fact]
    public async Task SyncFallback_RedirectedOutput_Succeeds()
    {
        // Redirected output falls back to sync path — same as production piped output.
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, CreateWorkItem(2, "Other Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateRedirectedPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_RendersContextAndSprintItems()
    {
        // TTY environment → async path exercises StreamWorkspaceData → RenderWorkspaceAsync → RenderHints
        var active = CreateWorkItem(1, "Active Item");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active, CreateWorkItem(2, "Other Item") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        // Verify the async renderer populated the table
        var output = _testConsole.Output;
        output.ShouldContain("Active Item");
        output.ShouldContain("Other Item");
        output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task AsyncPath_PopulatesClosureVariables_ForHintComputation()
    {
        // Verifies the closure-captured variables are populated after stream consumption
        // so that Workspace.Build + HintEngine work correctly.
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
        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService, _config,
            _formatterFactory, hintEngine, _processTypeStore, pipelineFactory);

        var result = await cmd.ExecuteAsync("human");

        result.ShouldBe(0);

        // The async path should have rendered the workspace and hints
        var output = _testConsole.Output;
        output.ShouldContain("Dirty Item");
        // Dirty items hint should appear (1 dirty item)
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

        var cmd = CreateCommand(CreateTtyPipelineFactory());
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

        var cmd = CreateCommand(CreateTtyPipelineFactory());
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

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", noLive: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_AllMode_UsesSyncPath()
    {
        // --all mode always uses sync path (hierarchy building requires full data)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        var result = await cmd.ExecuteAsync("human", all: true);

        result.ShouldBe(0);
    }

    [Fact]
    public async Task AsyncPath_VerifiesDataFetchSequence()
    {
        // Verifies the StreamWorkspaceData local function calls repositories in correct order
        var active = CreateWorkItem(1, "Active");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        // Set fresh cache timestamp so stale-while-revalidate does NOT trigger (EPIC-006)
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.ToString("O"));

        var cmd = CreateCommand(CreateTtyPipelineFactory());
        await cmd.ExecuteAsync("human");

        // Async path should have called these repositories (via StreamWorkspaceData closure)
        await _contextStore.Received(1).GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIdAsync(1, Arg.Any<CancellationToken>());
        await _iterationService.Received(1).GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).GetSeedsAsync(Arg.Any<CancellationToken>());
    }

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

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, CancellationToken.None);

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

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, CancellationToken.None);

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

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, CancellationToken.None);

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

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, CancellationToken.None);

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

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, CancellationToken.None);

        // Refresh completed — output should show the context caption
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

    private static async IAsyncEnumerable<WorkspaceDataChunk> CreateChunksAsync(
        params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
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
}
