using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class CacheRefreshTests
{
    private readonly TestConsole _testConsole = new();
    private readonly SpectreRenderer _spectreRenderer;
    public CacheRefreshTests() => _spectreRenderer = new(_testConsole, new SpectreTheme(new DisplayConfig()));

    [Fact]
    public async Task SpectreRenderer_RefreshStarted_ClearsRowsAndShowsBadge()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(CreateWorkItem(1, "Active")),
            new SprintItemsLoaded(new[] { CreateWorkItem(10, "Old Task") }),
            new SeedsLoaded(Array.Empty<WorkItem>()), new RefreshStarted(),
            new SprintItemsLoaded(new[] { CreateWorkItem(10, "Refreshed Task") }),
            new SeedsLoaded(Array.Empty<WorkItem>()), new RefreshCompleted());
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Refreshed Task");
        _testConsole.Output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshStarted_ShowsRefreshingBadge()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(CreateWorkItem(1, "Active")),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(Array.Empty<WorkItem>()),
            new RefreshStarted(), new SprintItemsLoaded(Array.Empty<WorkItem>()),
            new SeedsLoaded(Array.Empty<WorkItem>()), new RefreshCompleted());
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("refreshing");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshCompleted_RestoresCaption()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(CreateWorkItem(1, "Active Item")),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(Array.Empty<WorkItem>()),
            new RefreshStarted(), new SprintItemsLoaded(Array.Empty<WorkItem>()),
            new SeedsLoaded(Array.Empty<WorkItem>()), new RefreshCompleted());
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Active: #1 Active Item");
    }

    [Fact]
    public async Task SpectreRenderer_RefreshWithSeeds_ShowsSeedsAfterRefresh()
    {
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Fresh Seed", State = "New", IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow, IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var chunks = CreateChunksAsync(new ContextLoaded(null),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(Array.Empty<WorkItem>()),
            new RefreshStarted(), new SprintItemsLoaded(Array.Empty<WorkItem>()),
            new SeedsLoaded(new[] { seed }), new RefreshCompleted());
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Fresh Seed");
    }

    [Fact]
    public void DisplayConfig_CacheStaleMinutes_DefaultIs5() => new DisplayConfig().CacheStaleMinutes.ShouldBe(5);

    [Fact]
    public void TwigConfiguration_SetValue_CacheStaleMinutes()
    {
        var config = new TwigConfiguration();
        config.SetValue("display.cacheStaleMinutes", "10").ShouldBeTrue();
        config.Display.CacheStaleMinutes.ShouldBe(10);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    public void TwigConfiguration_SetValue_CacheStaleMinutes_RejectsInvalidValue(string value) =>
        new TwigConfiguration().SetValue("display.cacheStaleMinutes", value).ShouldBeFalse();

    private static async IAsyncEnumerable<WorkspaceDataChunk> CreateChunksAsync(params WorkspaceDataChunk[] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id, Title = title, Type = WorkItemType.Task, State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
