using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Verifies that the slimmed SetCommand does NOT perform working set eviction or sync.
/// These tests guard against regressions that would re-add sync/eviction behavior.
/// </summary>
public class WorkingSetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly ActiveItemResolver _activeItemResolver;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public WorkingSetCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _activeItemResolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private SetCommand CreateCommand(RenderingPipelineFactory? pipelineFactory = null)
    {
        var effectivePipeline = pipelineFactory
            ?? new RenderingPipelineFactory(_formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(effectivePipeline, _formatterFactory, _hintEngine, new TwigConfiguration());
        return new(ctx, _workItemRepo, _contextStore, _activeItemResolver);
    }

    [Fact]
    public async Task CacheMiss_NoEviction()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "New Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);


        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // Slimmed set does not evict
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheHit_NoSync()
    {
        var item = CreateWorkItem(42, "Cached Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // No eviction
        await _workItemRepo.DidNotReceive().EvictExceptAsync(
            Arg.Any<IReadOnlySet<int>>(), Arg.Any<CancellationToken>());
        // No ADO fetch (item was in cache, no sync)
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoFetchChildrenAsync_Called()
    {
        var item = CreateWorkItem(42, "Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var cmd = CreateCommand();
        await cmd.ExecuteAsync("42");

        await _adoService.DidNotReceive().FetchChildrenAsync(
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtyPath_EmitsConfirmation_NotDashboard()
    {
        var item = CreateWorkItem(42, "TTY Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var mockRenderer = Substitute.For<IAsyncRenderer>();
        var pipelineFactory = new RenderingPipelineFactory(
            _formatterFactory, mockRenderer, isOutputRedirected: () => false);
        var cmd = CreateCommand(pipelineFactory);
        var result = await cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        // Slimmed set emits confirmation line — no dashboard rendering via renderer
        await mockRenderer.DidNotReceiveWithAnyArgs().RenderStatusAsync(
            default!, default!, default);
    }

    private static WorkItem CreateWorkItem(int id, string title, int? parentId = null, string iterationPath = "Project\\Sprint 1")
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            ParentId = parentId,
            IterationPath = IterationPath.Parse(iterationPath).Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
