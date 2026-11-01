using Shouldly;
using Spectre.Console.Testing;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.ReadModels;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

// Shared legacy renderer coverage survives the CLI workspace-owner replacement.
public class WorkspaceCommandTests
{
    private readonly TestConsole _testConsole = new();
    private readonly SpectreRenderer _spectreRenderer;

    public WorkspaceCommandTests() =>
        _spectreRenderer = new(_testConsole, new SpectreTheme(new DisplayConfig()));

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsLoadingThenData()
    {
        var chunks = CreateChunksAsync(
            new ContextLoaded(CreateWorkItem(1, "Active")),
            new SprintItemsLoaded(new[] { CreateWorkItem(10, "Task A"), CreateWorkItem(20, "Task B") }),
            new SeedsLoaded(Array.Empty<WorkItem>()));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Task A");
        _testConsole.Output.ShouldContain("Task B");
        _testConsole.Output.ShouldContain("Active: #1");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsSeeds()
    {
        var seed = new WorkItem
        {
            Id = -1, Type = WorkItemType.Task, Title = "Seed Task", State = "New",
            IsSeed = true, SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var chunks = CreateChunksAsync(new ContextLoaded(null),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(new[] { seed }));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Seed Task");
        _testConsole.Output.ShouldContain("Seeds");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_ShowsStaleSeedWarning()
    {
        var seed = new WorkItem
        {
            Id = -2, Type = WorkItemType.Task, Title = "Stale Seed", State = "",
            IsSeed = true, SeedCreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        var chunks = CreateChunksAsync(new ContextLoaded(null),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(new[] { seed }));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Stale Seed");
        _testConsole.Output.ShouldContain("stale");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_NoContext_ShowsNoActiveContext()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(null),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(Array.Empty<WorkItem>()));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("No active context");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_RefreshBadge()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(CreateWorkItem(1, "Active")),
            new SprintItemsLoaded(Array.Empty<WorkItem>()), new SeedsLoaded(Array.Empty<WorkItem>()),
            new RefreshStarted(), new RefreshCompleted());
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Active: #1");
    }

    [Fact]
    public void SpectreRenderer_RenderHints_WritesHints()
    {
        _spectreRenderer.RenderHints(new List<string> { "Try: twig status", "3 dirty items" });
        _testConsole.Output.ShouldContain("twig status");
        _testConsole.Output.ShouldContain("dirty items");
    }

    [Fact]
    public void SpectreRenderer_RenderHints_EmptyList_NoOutput()
    {
        _spectreRenderer.RenderHints(Array.Empty<string>());
        _testConsole.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task SpectreRenderer_RenderStatusAsync_ShowsSummaryHeader()
    {
        var item = CreateWorkItem(1, "Summary Test");
        await _spectreRenderer.RenderStatusAsync(
            getItem: () => Task.FromResult<WorkItem?>(item),
            getPendingChanges: () => Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>()),
            ct: CancellationToken.None);
        _testConsole.Output.ShouldContain("#1");
        _testConsole.Output.ShouldContain("●");
        _testConsole.Output.ShouldContain("Summary Test");
        _testConsole.Output.ShouldContain("Task");
        _testConsole.Output.ShouldContain("New");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_HighlightsActiveItem()
    {
        var active = CreateWorkItem(1, "Active Item");
        var chunks = CreateChunksAsync(new ContextLoaded(active),
            new SprintItemsLoaded(new[] { active, CreateWorkItem(2, "Other Item") }),
            new SeedsLoaded(Array.Empty<WorkItem>()));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("►");
        _testConsole.Output.ShouldContain("Active Item");
        _testConsole.Output.ShouldContain("Other Item");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_NoContext_NoHighlight()
    {
        var chunks = CreateChunksAsync(new ContextLoaded(null),
            new SprintItemsLoaded(new[] { CreateWorkItem(1, "Item No Context") }),
            new SeedsLoaded(Array.Empty<WorkItem>()));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Item No Context");
        _testConsole.Output.ShouldNotContain("►");
    }

    [Fact]
    public async Task SpectreRenderer_RenderWorkspaceAsync_SprintBeforeContext_NoHighlight()
    {
        var active = CreateWorkItem(1, "Active Item");
        var chunks = CreateChunksAsync(new SprintItemsLoaded(new[] { active, CreateWorkItem(2, "Other Item") }),
            new ContextLoaded(active), new SeedsLoaded(Array.Empty<WorkItem>()));
        await _spectreRenderer.RenderWorkspaceAsync(chunks, 14, false, CancellationToken.None);
        _testConsole.Output.ShouldContain("Active Item");
        _testConsole.Output.ShouldContain("Other Item");
        _testConsole.Output.ShouldNotContain("►");
    }

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
        Id = id, Type = WorkItemType.Task, Title = title, State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
