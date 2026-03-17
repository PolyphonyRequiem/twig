using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SetCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IContextStore _contextStore;
    private readonly SetCommand _cmd;

    public SetCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _contextStore = Substitute.For<IContextStore>();
        _adoService.FetchChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _cmd = new SetCommand(_workItemRepo, _adoService, _contextStore,
            formatterFactory, hintEngine);
    }

    [Fact]
    public async Task Set_NumericId_FromCache_SetsContext()
    {
        var item = CreateWorkItem(42, "Test Item");
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_NumericId_FetchesFromAdo_WhenNotCached()
    {
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        var item = CreateWorkItem(42, "Fetched Item");
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var result = await _cmd.ExecuteAsync("42");

        result.ShouldBe(0);
        await _adoService.Received().FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(item, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_SingleMatch_SetsContext()
    {
        var item = CreateWorkItem(10, "Fix login bug");
        _workItemRepo.FindByPatternAsync("login", Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var result = await _cmd.ExecuteAsync("login");

        result.ShouldBe(0);
        await _contextStore.Received().SetActiveWorkItemIdAsync(10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_MultiMatch_ReturnsError()
    {
        var items = new[]
        {
            CreateWorkItem(10, "Fix login page"),
            CreateWorkItem(11, "Login timeout bug"),
        };
        _workItemRepo.FindByPatternAsync("login", Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await _cmd.ExecuteAsync("login");

        result.ShouldBe(1);
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_Pattern_NoMatch_ReturnsError()
    {
        _workItemRepo.FindByPatternAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var result = await _cmd.ExecuteAsync("nonexistent");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Set_EmptyInput_ReturnsUsageError()
    {
        var result = await _cmd.ExecuteAsync("");

        result.ShouldBe(2);
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
