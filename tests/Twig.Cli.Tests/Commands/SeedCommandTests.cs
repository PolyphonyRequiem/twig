using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class SeedCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly ActiveItemResolver _resolver;
    private readonly SeedCommand _cmd;

    private static StateEntry[] ToStateEntries(params string[] names) =>
        names.Select(n => new StateEntry(n, StateCategory.Unknown, null)).ToArray();

    private static ProcessTypeRecord MakeRecord(string typeName, string[] states, string[] childTypes) =>
        new()
        {
            TypeName = typeName,
            States = ToStateEntries(states),
            ValidChildTypes = childTypes,
        };

    private static ProcessConfiguration BuildAgileConfig() =>
        ProcessConfiguration.FromRecords(new[]
        {
            MakeRecord("Epic", new[] { "New", "Active", "Closed", "Removed" }, new[] { "Feature" }),
            MakeRecord("Feature", new[] { "New", "Active", "Closed", "Removed" }, new[] { "User Story", "Bug" }),
            MakeRecord("User Story", new[] { "New", "Active", "Resolved", "Closed", "Removed" }, new[] { "Task" }),
            MakeRecord("Bug", new[] { "New", "Active", "Resolved", "Closed" }, new[] { "Task" }),
            MakeRecord("Task", new[] { "New", "Active", "Closed", "Removed" }, Array.Empty<string>()),
        });

    public SeedCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();

        _processConfigProvider.GetConfiguration()
            .Returns(BuildAgileConfig());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new SeedCommand(_resolver, _workItemRepo, _adoService, _processConfigProvider,
            formatterFactory, hintEngine);
    }

    [Fact]
    public async Task Seed_ValidTitle_CreatesAndPushes()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(100);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(100, "New Story", WorkItemType.UserStory));

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(0);
        await _adoService.Received().CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seed_NoActiveContext_ReturnsErrorWhenNoTypeOverride()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("New Item");

        result.ShouldBe(1); // No parent and no type = error
    }

    [Fact]
    public async Task Seed_InvalidParentChildType_ReturnsError()
    {
        var parent = CreateWorkItem(1, "Parent Task", WorkItemType.Task);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);

        // Task can't have Feature children in Agile
        var result = await _cmd.ExecuteAsync("Child Feature", type: "Feature");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Seed_TypeOverride_UsesSpecifiedType()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(100);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(100, "New Bug", WorkItemType.Bug));

        var result = await _cmd.ExecuteAsync("New Bug", type: "Bug");

        result.ShouldBe(0);
        await _adoService.Received().CreateAsync(
            Arg.Is<WorkItem>(w => w.Type == WorkItemType.Bug),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seed_EmptyTitle_ReturnsError()
    {
        var result = await _cmd.ExecuteAsync("");

        result.ShouldBe(2);
    }

    [Fact]
    public async Task Seed_CacheMiss_AutoFetchesFromAdo()
    {
        var parent = CreateWorkItem(1, "Parent Feature", WorkItemType.Feature);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.CreateAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>()).Returns(100);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(100, "New Story", WorkItemType.UserStory));

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(0);
        // Auto-fetch saved the parent to cache
        await _workItemRepo.Received().SaveAsync(parent, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seed_Unreachable_ReturnsErrorWithReason()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns<WorkItem>(x => throw new InvalidOperationException("Network timeout"));

        var result = await _cmd.ExecuteAsync("New Story");

        result.ShouldBe(1);
    }

    private static WorkItem CreateWorkItem(int id, string title, WorkItemType type)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
