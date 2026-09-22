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
    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(160)]
    public async Task MetadataRemainsInSeparateColumnsWhenTitleWraps(int width)
    {
        var item = new WorkItemBuilder(10, "A long title that must give space to the state and freshness columns")
            .InState("Active")
            .LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-15)).Build();
        var output = await Render(width, 5, item);
        output.ShouldNotContain("cached");
        var lines = output.Split('\n');
        var header = lines.First(line => line.Contains("Title") && line.Contains("State"));
        var headings = header.Split('│').Select(cell => cell.Trim()).ToArray();
        var stateColumn = Array.IndexOf(headings, "State");
        var ageColumn = Array.IndexOf(headings, "Age");
        stateColumn.ShouldBeGreaterThan(0);
        ageColumn.ShouldBeGreaterThan(stateColumn);
        var row = lines.First(line => line.Contains("15m ago")).Split('│');
        row[stateColumn].ShouldContain("Active");
        row[ageColumn].Trim().ShouldBe("15m ago");
    }

    [Fact]
    public async Task FreshAndUnknownAgesStayBlankWhileStaleAgeIsVisible()
    {
        var fresh = new WorkItemBuilder(11, "Fresh").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-2)).Build();
        var stale = new WorkItemBuilder(12, "Stale").LastSyncedAt(DateTimeOffset.UtcNow.AddMinutes(-15)).Build();
        var unknown = new WorkItemBuilder(13, "Unknown").Build();
        var output = await Render(120, 5, fresh, stale, unknown);
        var header = output.Split('\n').First(line => line.Contains("Title") && line.Contains("Age"));
        var ageColumn = Array.IndexOf(header.Split('│').Select(cell => cell.Trim()).ToArray(), "Age");
        foreach (var title in new[] { "Fresh", "Unknown" })
            output.Split('\n').First(line => line.Contains(title)).Split('│')[ageColumn].Trim().ShouldBeEmpty();
        output.ShouldContain("15m ago");
        (await Render(120, 20, stale)).ShouldNotContain("15m ago");
    }

    private static async Task<string> Render(int width, int threshold, params WorkItem[] items)
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        var renderer = new SpectreRenderer(console, new SpectreTheme(new DisplayConfig()));
        await renderer.RenderWorkspaceAsync(Chunks(items), 14, false, CancellationToken.None, cacheStaleMinutes: threshold);
        return console.Output;
    }

    private static async IAsyncEnumerable<WorkspaceDataChunk> Chunks(WorkItem[] items)
    {
        yield return new ContextLoaded(null);
        yield return new SprintItemsLoaded(items);
        yield return new SeedsLoaded(Array.Empty<WorkItem>());
        await Task.CompletedTask;
    }
}
