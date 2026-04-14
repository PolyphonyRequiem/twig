using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class BuildStatusViewCacheAgeTests
{
    private readonly TestConsole _testConsole;
    private readonly SpectreRenderer _renderer;

    public BuildStatusViewCacheAgeTests()
    {
        _testConsole = new TestConsole();
        _testConsole.Profile.Width = 120;
        _renderer = new SpectreRenderer(_testConsole, new SpectreTheme(new DisplayConfig()));
    }

    // ── Cache-age indicator ──────────────────────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_StaleItem_ShowsCacheAge()
    {
        var item = CreateBuilder(100, "Stale Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-15))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldContain("cached 15m ago");
    }

    [Fact]
    public async Task BuildStatusViewAsync_FreshItem_NoCacheAge()
    {
        var item = CreateBuilder(101, "Fresh Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-2))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NullLastSyncedAt_NoCacheAge()
    {
        var item = CreateBuilder(102, "Never Synced")
            .InState("Active")
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task BuildStatusViewAsync_StaleHours_ShowsHoursFormat()
    {
        var item = CreateBuilder(103, "Hours Stale")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddHours(-3))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldContain("cached 3h ago");
    }

    [Fact]
    public async Task BuildStatusViewAsync_StaleDays_ShowsDaysFormat()
    {
        var item = CreateBuilder(104, "Days Stale")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddDays(-2))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldContain("cached 2d ago");
    }

    [Fact]
    public async Task BuildStatusViewAsync_CacheAgeAppearsInSummaryAndHeader()
    {
        var item = CreateBuilder(105, "Dual Age")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-10))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        // Should appear at least twice — once in summary line, once in panel header
        var count = CountOccurrences(output, "cached 10m ago");
        count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task BuildStatusViewAsync_ExactlyAtThreshold_NoCacheAge()
    {
        var item = CreateBuilder(106, "Boundary Item")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-4))
            .Build();

        var output = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);

        output.ShouldNotContain("cached");
    }

    [Fact]
    public async Task BuildStatusViewAsync_CustomStaleMinutes_Respected()
    {
        var item = CreateBuilder(107, "Custom Threshold")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-8))
            .Build();

        // With 10-minute threshold, 8 minutes is fresh
        var outputFresh = await RenderStatusViewAsync(item, cacheStaleMinutes: 10);
        outputFresh.ShouldNotContain("cached");

        // Reset console for second render
        _testConsole.Clear(false);

        // With 5-minute threshold, 8 minutes is stale
        var outputStale = await RenderStatusViewAsync(item, cacheStaleMinutes: 5);
        outputStale.ShouldContain("cached 8m ago");
    }

    // ── Dirty-state indicators ──────────────────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_DirtyItem_ShowsBulletIndicator()
    {
        var item = CreateBuilder(200, "Dirty Item")
            .InState("Active")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(200, "field", "System.Title", "Old Title", "Dirty Item")
        };

        var output = await RenderStatusViewAsync(item, changes: changes);

        // ● indicator should be present (DD-03)
        output.ShouldContain("●");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DirtyItem_ShowsDirtySummary()
    {
        var item = CreateBuilder(201, "Dirty Summary")
            .InState("Active")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(201, "field", "System.Title", "Old Title", "Dirty Summary")
        };

        var output = await RenderStatusViewAsync(item, changes: changes);

        output.ShouldContain("local: Title changed");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DirtyItem_ShowsUnsavedTooltip()
    {
        var item = CreateBuilder(202, "Unsaved Item")
            .InState("Active")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(202, "field", "System.Title", "Old", "New")
        };

        var output = await RenderStatusViewAsync(item, changes: changes);

        output.ShouldContain("unsaved");
        output.ShouldContain("twig save");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DirtyWithStateChange_ShowsStateTransition()
    {
        var item = CreateBuilder(203, "State Change")
            .InState("Doing")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(203, "state", "System.State", "New", "Doing")
        };

        var output = await RenderStatusViewAsync(item, changes: changes);

        output.ShouldContain("local: State New");
        output.ShouldContain("Doing");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DirtyWithMultipleChanges_ShowsAggregatedSummary()
    {
        var item = CreateBuilder(204, "Multi Changes")
            .InState("Active")
            .Dirty()
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(204, "field", "System.Title", "Old", "New"),
            new(204, "field", "System.Description", "Old desc", "New desc"),
            new(204, "note", null, null, "My note")
        };

        var output = await RenderStatusViewAsync(item, changes: changes);

        output.ShouldContain("local: 2 field changes, 1 note");
    }

    [Fact]
    public async Task BuildStatusViewAsync_CleanItem_NoDirtyIndicator()
    {
        var item = CreateBuilder(205, "Clean Item")
            .InState("Active")
            .Build();

        var output = await RenderStatusViewAsync(item);

        output.ShouldNotContain("local:");
        output.ShouldNotContain("unsaved");
    }

    [Fact]
    public async Task BuildStatusViewAsync_NoPendingChanges_NoDirtySummary()
    {
        var item = CreateBuilder(206, "No Changes")
            .InState("Active")
            .Build();

        var output = await RenderStatusViewAsync(item, changes: []);

        output.ShouldNotContain("local:");
        output.ShouldNotContain("unsaved");
    }

    // ── Combined cache-age + dirty ──────────────────────────────────

    [Fact]
    public async Task BuildStatusViewAsync_StaleAndDirty_ShowsBothIndicators()
    {
        var item = CreateBuilder(300, "Stale and Dirty")
            .InState("Active")
            .Dirty()
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-20))
            .Build();
        var changes = new List<PendingChangeRecord>
        {
            new(300, "field", "System.Title", "Old", "Stale and Dirty")
        };

        var output = await RenderStatusViewAsync(item, changes: changes, cacheStaleMinutes: 5);

        output.ShouldContain("cached 20m ago");
        output.ShouldContain("local: Title changed");
        output.ShouldContain("●");
    }

    [Fact]
    public async Task BuildStatusViewAsync_DefaultCacheStaleMinutes_Is5()
    {
        var item = CreateBuilder(301, "Default Threshold")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-6))
            .Build();

        // Uses default cacheStaleMinutes (5), so 6 minutes is stale
        var output = await RenderStatusViewAsync(item);

        output.ShouldContain("cached 6m ago");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static WorkItemBuilder CreateBuilder(int id, string title) =>
        new WorkItemBuilder(id, title)
            .WithIterationPath("Project\\Sprint 1")
            .WithAreaPath("Project");

    private async Task<string> RenderStatusViewAsync(
        WorkItem item,
        List<PendingChangeRecord>? changes = null,
        int cacheStaleMinutes = 5)
    {
        var renderable = await _renderer.BuildStatusViewAsync(
            item,
            () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(changes ?? []),
            cacheStaleMinutes: cacheStaleMinutes);
        _testConsole.Write(renderable);
        return _testConsole.Output;
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
