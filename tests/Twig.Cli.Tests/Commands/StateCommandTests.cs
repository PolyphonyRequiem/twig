using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class StateCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IProcessConfigurationProvider _processConfigProvider;
    private readonly IConsoleInput _consoleInput;
    private readonly StateCommand _cmd;

    public StateCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new StateCommand(
            resolver, _workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            formatterFactory, hintEngine);
    }

    [Fact]
    public async Task State_ForwardTransition_AutoApplies()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active"); // Active (forward from New)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_AlreadyInState_NoOp()
    {
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Active"); // Active, already Active

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_InvalidState_ReturnsError()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Nonexistent"); // no match

        result.ShouldBe(1);
    }

    [Fact]
    public async Task State_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task State_AutoPushesNotes_OnStateChange()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var pendingNote = new PendingChangeRecord(1, "note", null, null, "Test note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "Test note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_PreservesFieldChanges_OnStateChange()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var pendingField = new PendingChangeRecord(1, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingField });

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // Field changes should NOT be cleared
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_BackwardTransition_UserConfirms_AppliesChange()
    {
        // Active → New is a backward transition for Agile UserStory
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync("New"); // New (backward from Active)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_BackwardTransition_UserDeclines_Cancels()
    {
        // Active → New is a backward transition for Agile UserStory
        var item = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        SetupActiveItem(item);
        _consoleInput.ReadLine().Returns("n");

        var result = await _cmd.ExecuteAsync("New"); // New (backward from Active)

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_CutTransition_UserConfirms_AppliesChange()
    {
        // New → Removed is a Cut transition for Agile UserStory
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _consoleInput.ReadLine().Returns("y");

        var result = await _cmd.ExecuteAsync("Removed"); // Removed (cut from New)

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title, string state, WorkItemType type)
    {
        return new WorkItem
        {
            Id = id,
            Type = type,
            Title = title,
            State = state,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
