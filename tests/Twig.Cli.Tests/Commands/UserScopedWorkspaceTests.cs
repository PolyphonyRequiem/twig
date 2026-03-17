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

public class UserScopedWorkspaceTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IIterationService _iterationService;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public UserScopedWorkspaceTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _iterationService = Substitute.For<IIterationService>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();

        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    [Fact]
    public async Task Ws_DefaultMode_FiltersToUserWhenConfigured()
    {
        var config = new TwigConfiguration();
        config.User.DisplayName = "Alice Smith";

        var aliceItem = CreateWorkItem(1, "Task A", "Alice Smith");
        var bobItem = CreateWorkItem(2, "Task B", "Bob Jones");

        _workItemRepo.GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Is("Alice Smith"), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem });
        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem, bobItem });

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Should call the assignee-scoped method, not the full iteration method
        await _workItemRepo.Received(1).GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Is("Alice Smith"), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ws_AllFlag_ShowsAllTeamItems()
    {
        var config = new TwigConfiguration();
        config.User.DisplayName = "Alice Smith";

        var aliceItem = CreateWorkItem(1, "Task A", "Alice Smith");
        var bobItem = CreateWorkItem(2, "Task B", "Bob Jones");

        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem, bobItem });

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // Should call the full iteration method, not the assignee-scoped one
        await _workItemRepo.Received(1).GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ws_NoUserConfigured_FallsBackToAllItems()
    {
        var config = new TwigConfiguration(); // No user configured

        var aliceItem = CreateWorkItem(1, "Task A", "Alice Smith");
        var bobItem = CreateWorkItem(2, "Task B", "Bob Jones");

        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem, bobItem });

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Should fall back to full iteration method
        await _workItemRepo.Received(1).GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sprint_Command_ShowsAllItems_GroupedByAssignee()
    {
        var config = new TwigConfiguration();
        config.User.DisplayName = "Alice Smith";

        var aliceItem = CreateWorkItem(1, "Task A", "Alice Smith");
        var bobItem = CreateWorkItem(2, "Task B", "Bob Jones");

        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(new[] { aliceItem, bobItem });

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        // Use StringWriter to capture output without modifying global Console.Out.
        // FormatSprintView returns a string; we call it directly to avoid Console.SetOut.
        var formatter = _formatterFactory.GetFormatter("human");
        var workspace = Domain.ReadModels.Workspace.Build(null, new[] { aliceItem, bobItem }, Array.Empty<Domain.Aggregates.WorkItem>());
        var output = formatter.FormatSprintView(workspace, config.Seed.StaleDays);

        // Verify sprint view format is used — it groups by assignee
        output.ShouldContain("Sprint");
        output.ShouldNotContain("Workspace");

        // Verify both assignee group headers are present (the key discriminator for grouped output)
        output.ShouldContain("Alice Smith");
        output.ShouldContain("Bob Jones");

        // Also verify the command routes correctly via --all
        var result = await cmd.ExecuteAsync(all: true);
        result.ShouldBe(0);
        await _workItemRepo.Received(1).GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ws_EmptyUserDisplayName_FallsBackToAllItems()
    {
        var config = new TwigConfiguration();
        config.User.DisplayName = "   "; // Whitespace only

        _workItemRepo.GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received(1).GetByIterationAsync(
            Arg.Any<IterationPath>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ws_WithActiveContext_ShowsContextInBothModes()
    {
        var config = new TwigConfiguration();
        config.User.DisplayName = "Alice Smith";

        var contextItem = CreateWorkItem(10, "My Active Item", "Alice Smith");
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(contextItem);
        _workItemRepo.GetByIterationAndAssigneeAsync(
            Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { contextItem });

        var cmd = new WorkspaceCommand(_contextStore, _workItemRepo, _iterationService,
            config, _formatterFactory, _hintEngine, _processTypeStore);

        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    private static WorkItem CreateWorkItem(int id, string title, string? assignedTo = null)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "Active",
            AssignedTo = assignedTo,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
