using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public sealed class WorkspaceCacheAgeTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public WorkspaceCacheAgeTests()
    {
        _testConsole = new TestConsole();
        _testConsole.Profile.Width = 120;
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Stale sprint items show cache-age ────────────────────────────

    [Fact]
    public async Task SprintItem_Stale_ShowsCacheAge()
    {
        var item = new WorkItemBuilder(10, "Stale Sprint Task")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-15))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldContain("cached 15m ago");
    }

    [Fact]
    public async Task SprintItem_Fresh_NoCacheAge()
    {
        var item = new WorkItemBuilder(11, "Fresh Sprint Task")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-2))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task SprintItem_NullLastSyncedAt_NoCacheAge()
    {
        var item = new WorkItemBuilder(12, "Never Synced Task")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    // ── Time unit formatting ─────────────────────────────────────────

    [Fact]
    public async Task SprintItem_StaleHours_ShowsHoursFormat()
    {
        var item = new WorkItemBuilder(13, "Hours Old")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddHours(-3))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldContain("cached 3h ago");
    }

    [Fact]
    public async Task SprintItem_StaleDays_ShowsDaysFormat()
    {
        var item = new WorkItemBuilder(14, "Days Old")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddDays(-2))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldContain("cached 2d ago");
    }

    // ── Threshold boundary ───────────────────────────────────────────

    [Fact]
    public async Task SprintItem_BelowThreshold_NoCacheAge()
    {
        var item = new WorkItemBuilder(15, "Boundary Fresh")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-4))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task SprintItem_CustomThreshold_Respected()
    {
        var item = new WorkItemBuilder(16, "Custom Threshold")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-8))
            .Build();

        // With 10-minute threshold, 8 minutes is fresh
        var outputFresh = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 10);
        outputFresh.ShouldNotContain("cached");

        _testConsole.Clear(false);

        // With 5-minute threshold, 8 minutes is stale
        var outputStale = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);
        outputStale.ShouldContain("cached 8m ago");
    }

    // ── Default parameter ────────────────────────────────────────────

    [Fact]
    public async Task SprintItem_DefaultCacheStaleMinutes_Is5()
    {
        var item = new WorkItemBuilder(17, "Default Threshold")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-6))
            .Build();

        // Uses default cacheStaleMinutes (5), so 6 minutes is stale
        var output = await RenderWorkspaceAsync(new[] { item });

        output.ShouldContain("cached 6m ago");
    }

    // ── Multiple items: mixed staleness ──────────────────────────────

    [Fact]
    public async Task MultipleItems_MixedStaleness_OnlyStaleShowsCacheAge()
    {
        var staleItem = new WorkItemBuilder(18, "Stale One")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-30))
            .Build();

        var freshItem = new WorkItemBuilder(19, "Fresh One")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-1))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { staleItem, freshItem }, cacheStaleMinutes: 5);

        // Stale item shows cache-age; fresh item does not
        output.ShouldContain("cached 30m ago");
        output.ShouldNotContain("cached 1m ago");
        output.ShouldContain("Stale One");
        output.ShouldContain("Fresh One");
    }

    // ── Cache-age appears alongside title, not in other columns ──────

    [Fact]
    public async Task SprintItem_CacheAge_AppearsOnSameRowAsTitle()
    {
        var item = new WorkItemBuilder(20, "Row Check")
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-10))
            .Build();

        var output = await RenderWorkspaceAsync(new[] { item }, cacheStaleMinutes: 5);

        // Both the title and cache-age should be present
        output.ShouldContain("Row Check");
        output.ShouldContain("cached 10m ago");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<string> RenderWorkspaceAsync(
        WorkItem[] sprintItems,
        int cacheStaleMinutes = 5)
    {
        var chunks = CreateChunksAsync(
            new WorkspaceDataChunk.ContextLoaded(null),
            new WorkspaceDataChunk.SprintItemsLoaded(sprintItems),
            new WorkspaceDataChunk.SeedsLoaded(Array.Empty<WorkItem>()));

        await _renderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None,
            cacheStaleMinutes: cacheStaleMinutes);

        return _testConsole.Output;
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

}
