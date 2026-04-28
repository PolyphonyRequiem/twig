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

namespace Twig.Cli.Tests.Rendering;

public class CacheRefreshTests
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
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _spectreRenderer;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public CacheRefreshTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _config = new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 5 }
        };
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

        _testConsole = new TestConsole();
        _spectreRenderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = true });
    }

    private RenderingPipelineFactory CreateTtyPipelineFactory() =>
        new(_formatterFactory, _spectreRenderer, isOutputRedirected: () => false);

    private WorkspaceCommand CreateCommand(RenderingPipelineFactory pipelineFactory) =>
        new(new CommandContext(pipelineFactory, _formatterFactory, _hintEngine, _config),
            _contextStore, _workItemRepo, _iterationService,
            _processTypeStore, _fieldDefinitionStore,
            _activeItemResolver, _workingSetService, _trackingService, new SprintHierarchyBuilder(),
            new SprintIterationResolver(_iterationService, _workItemRepo));

    // ── IsCacheStale unit tests ─────────────────────────────────────

    [Fact]
    public void IsCacheStale_NullTimestamp_ReturnsTrue()
    {
        WorkspaceCommand.IsCacheStale(null, 5).ShouldBeTrue();
    }

    [Fact]
    public void IsCacheStale_InvalidTimestamp_ReturnsTrue()
    {
        WorkspaceCommand.IsCacheStale("not-a-date", 5).ShouldBeTrue();
    }

    [Fact]
    public void IsCacheStale_FreshTimestamp_ReturnsFalse()
    {
        var recent = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
        WorkspaceCommand.IsCacheStale(recent, 5).ShouldBeFalse();
    }

    [Fact]
    public void IsCacheStale_StaleTimestamp_ReturnsTrue()
    {
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        WorkspaceCommand.IsCacheStale(old, 5).ShouldBeTrue();
    }

    [Fact]
    public void IsCacheStale_ExactBoundary_ReturnsFalse()
    {
        // Exactly at the boundary (5 minutes ago with 5-minute threshold) should not be stale
        var atBoundary = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1).ToString("O");
        WorkspaceCommand.IsCacheStale(atBoundary, 5).ShouldBeFalse();
    }

    // ── Stale-while-revalidate integration tests ────────────────────

    [Fact]
    public async Task StaleCache_YieldsRefreshStartedAndCompleted()
    {
        // Arrange: stale timestamp (10 minutes old, threshold is 5)
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(staleTimestamp);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Task A") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Task A");
        // Verify the refresh badge was shown (caption changes visible in output)
        output.ShouldContain("refreshing");
        // Verify last_refreshed_at was updated
        await _contextStore.Received(1).SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FreshCache_NoRefreshChunks()
    {
        // Arrange: fresh timestamp (1 minute old, threshold is 5)
        var freshTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(freshTimestamp);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Task A") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Task A");
        output.ShouldNotContain("refreshing");
        // Verify last_refreshed_at was NOT updated (no refresh needed)
        await _contextStore.DidNotReceive().SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NullTimestamp_TreatedAsStale_TriggersRefresh()
    {
        // Arrange: no last_refreshed_at value (first run)
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Task A") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert
        result.ShouldBe(0);
        // Iteration service called twice: once for initial data, once for refresh
        await _iterationService.Received(2).GetCurrentIterationAsync(Arg.Any<CancellationToken>());
        // Timestamp was persisted after refresh
        await _contextStore.Received(1).SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleCache_RefreshUpdatesClosureVariables_ForHintComputation()
    {
        // Arrange: stale timestamp triggers refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        var active = CreateWorkItem(1, "Active");
        active.SetDirty();
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(active);

        // First call returns initial data, refresh returns updated data
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { active });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert — command completed successfully with hints computed from refreshed data
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Active");
    }

    // ── --no-refresh flag: skips sync pass ──────────────────────────

    [Fact]
    public async Task NoRefresh_SkipsSyncPass_RendersFromCacheOnly()
    {
        // Arrange: stale timestamp — would normally trigger refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Cached Task") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human", noRefresh: true);

        // Assert — command succeeds, data rendered, but no refresh badge
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Cached Task");
        output.ShouldNotContain("refreshing");
        // Verify last_refreshed_at was NOT read for staleness check
        await _contextStore.DidNotReceive().GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>());
        // Verify last_refreshed_at was NOT updated
        await _contextStore.DidNotReceive().SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoRefresh_IsIndependentOfNoLive()
    {
        // Arrange: stale timestamp — would normally trigger refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Task B") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Both flags set: noLive + noRefresh
        var result = await cmd.ExecuteAsync("human", noLive: true, noRefresh: true);

        result.ShouldBe(0);
    }

    // ── SpectreRenderer refresh badge tests ─────────────────────────

    [Fact]
    public async Task SpectreRenderer_RefreshStarted_ClearsRowsAndShowsBadge()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(CreateWorkItem(1, "Active")),
            new WorkspaceDataChunk.SprintItemsLoaded(new[]
            {
                CreateWorkItem(10, "Old Task"),
            }),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshStarted(),
            new WorkspaceDataChunk.SprintItemsLoaded(new[]
            {
                CreateWorkItem(10, "Refreshed Task"),
            }),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshCompleted());

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        // After refresh completed, the final output should contain refreshed data
        output.ShouldContain("Refreshed Task");
        // The original caption should be restored after refresh
        output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshStarted_ShowsRefreshingBadge()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(CreateWorkItem(1, "Active")),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshStarted(),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshCompleted());

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        // Verify refresh badge was displayed during refresh
        output.ShouldContain("refreshing");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshCompleted_RestoresCaption()
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(CreateWorkItem(1, "Active Item")),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshStarted(),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshCompleted());

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        // After refresh completes, the caption should be restored to the context
        output.ShouldContain("Active: #1 Active Item");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshWithSeeds_ShowsSeedsAfterRefresh()
    {
        var seed = new WorkItem
        {
            Id = -1,
            Type = WorkItemType.Task,
            Title = "Fresh Seed",
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };

        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.RefreshStarted(),
            new WorkspaceDataChunk.SprintItemsLoaded(Array.Empty<WorkItem>()),
            new WorkspaceDataChunk.SeedsLoaded(new[] { seed }),
            new WorkspaceDataChunk.RefreshCompleted());

        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);

        var output = _testConsole.Output;
        output.ShouldContain("Fresh Seed");
    }

    // ── Configuration tests ─────────────────────────────────────────

    [Fact]
    public void DisplayConfig_CacheStaleMinutes_DefaultIs5()
    {
        var config = new DisplayConfig();
        config.CacheStaleMinutes.ShouldBe(5);
    }

    [Fact]
    public void TwigConfiguration_SetValue_CacheStaleMinutes()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.cacheStaleMinutes", "10").ShouldBeTrue();
        config.Display.CacheStaleMinutes.ShouldBe(10);
    }

    [Fact]
    public void TwigConfiguration_SetValue_CacheStaleMinutes_InvalidValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.cacheStaleMinutes", "abc").ShouldBeFalse();
    }

    [Fact]
    public void TwigConfiguration_SetValue_CacheStaleMinutes_ZeroValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.cacheStaleMinutes", "0").ShouldBeFalse();
    }

    [Fact]
    public void TwigConfiguration_SetValue_CacheStaleMinutes_NegativeValue()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.cacheStaleMinutes", "-1").ShouldBeFalse();
    }

    // ── Stage 4 error handling tests ────────────────────────────────

    [Fact]
    public async Task StaleCache_RefreshFails_RestoresOriginalData()
    {
        // Arrange: stale timestamp triggers refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));

        // First call (Stage 2) returns cached data; second call (Stage 4 re-fetch) throws
        var iterationCallCount = 0;
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                iterationCallCount++;
                if (iterationCallCount > 1)
                    throw new InvalidOperationException("network timeout");
                return IterationPath.Parse("Project\\Sprint 1").Value;
            });

        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Cached Task") });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert — command succeeds and original data is visible (not an empty table)
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Cached Task");
        // Timestamp should NOT have been updated since refresh failed
        await _contextStore.DidNotReceive().SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleCache_SeedRefreshFails_RestoresOriginalData()
    {
        // Arrange: stale timestamp, re-fetch of seeds throws
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { CreateWorkItem(10, "Cached Task") });

        // First GetSeedsAsync (Stage 3) returns cached data; second (Stage 4) throws
        var seedCallCount = 0;
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seedCallCount++;
                if (seedCallCount > 1)
                    throw new InvalidOperationException("auth failure");
                return Array.Empty<WorkItem>();
            });

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert — command succeeds, badge removed, original rows restored
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Cached Task");
        // Timestamp should NOT have been updated since refresh failed
        await _contextStore.DidNotReceive().SetValueAsync(
            "last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleCache_TimestampPersistenceFails_StillDisplaysFreshData()
    {
        // Arrange: stale timestamp triggers refresh
        _contextStore.GetValueAsync("last_refreshed_at", Arg.Any<CancellationToken>())
            .Returns(DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O"));
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1, "Active"));

        // First GetByIterationAsync (Stage 2) returns cached data;
        // second (Stage 4 re-fetch) returns distinct refreshed data
        var iterationCallCount = 0;
        _workItemRepo.GetByIterationAsync(Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                iterationCallCount++;
                return iterationCallCount > 1
                    ? new[] { CreateWorkItem(10, "Refreshed Task") }
                    : new[] { CreateWorkItem(10, "Cached Task") };
            });
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // SetValueAsync throws — simulates persistence failure (e.g., disk full)
        _contextStore.SetValueAsync("last_refreshed_at", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("disk full")));

        var cmd = CreateCommand(CreateTtyPipelineFactory());

        // Act
        var result = await cmd.ExecuteAsync("human");

        // Assert — fresh data is displayed despite SetValueAsync throwing
        result.ShouldBe(0);
        var output = _testConsole.Output;
        output.ShouldContain("Refreshed Task");
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
