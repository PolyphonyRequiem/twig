using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Ado.Exceptions;
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
    private readonly StringWriter _stderr;
    private readonly StateCommand _cmd;

    public StateCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _processConfigProvider = Substitute.For<IProcessConfigurationProvider>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _stderr = new StringWriter();

        _processConfigProvider.GetConfiguration()
            .Returns(ProcessConfigBuilder.Agile());

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new StateCommand(
            resolver, _workItemRepo, _adoService,
            _pendingChangeStore, _processConfigProvider, _consoleInput,
            formatterFactory, hintEngine, stderr: _stderr);
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

    [Fact]
    public async Task State_ForwardTransition_ReFetchesAndSavesServerItem()
    {
        var local = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(local);

        // Distinct "server" item representing post-transition state
        var serverItem = CreateWorkItem(1, "Test", "Active", WorkItemType.UserStory);
        serverItem.MarkSynced(5);

        // First FetchAsync (conflict check) returns local; second (resync) returns serverItem
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(local, serverItem);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // FetchAsync called twice: conflict check + resync
        await _adoService.Received(2).FetchAsync(1, Arg.Any<CancellationToken>());
        // SaveAsync receives the re-fetched server item, not the local one
        await _workItemRepo.Received(1).SaveAsync(serverItem, Arg.Any<CancellationToken>());
        _stderr.ToString().ShouldNotContain("resync failed");
    }

    [Fact]
    public async Task State_PatchConflict_RetrySucceeds_ReturnsSuccess()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        // First patch attempt → conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        // Re-fetch returns fresh item at revision 5
        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        // FetchAsync: first call returns remote (pre-patch conflict check),
        // second returns freshItem (retry re-fetch from ConflictRetryHelper),
        // third returns freshItem again (resync after successful patch)
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        // Retry with fresh revision succeeds
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        result.ShouldBe(0);
        // PatchAsync called twice: once with old revision (conflict), once with fresh revision (success)
        await _adoService.Received(2).PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_PatchConflict_BothAttemptsFail_ThrowsAdoConflictException()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        var remote = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        remote.MarkSynced(3);

        // Re-fetch returns fresh item at revision 5
        var freshItem = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        freshItem.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshItem);

        // First patch attempt → conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        // Retry also conflicts
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        await Should.ThrowAsync<AdoConflictException>(
            () => _cmd.ExecuteAsync("Active"));
    }

    [Fact]
    public async Task State_Resync_NotAttempted_WhenTransitionFails()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (conflict check) succeeds
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(item);

        // PatchAsync throws a non-conflict failure
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unavailable"));

        await Should.ThrowAsync<HttpRequestException>(() => _cmd.ExecuteAsync("Active"));

        // FetchAsync called only once (conflict check), never for resync
        await _adoService.Received(1).FetchAsync(1, Arg.Any<CancellationToken>());
        // SaveAsync never called — transition failed before resync
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_Resync_FetchThrows_ReturnsSuccessWithWarning()
    {
        var item = CreateWorkItem(1, "Test", "New", WorkItemType.UserStory);
        SetupActiveItem(item);

        // First FetchAsync (conflict check) returns item; second (resync) throws
        var fetchCallCount = 0;
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fetchCallCount++;
                if (fetchCallCount == 1) return item;
                throw new HttpRequestException("network timeout");
            });

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await _cmd.ExecuteAsync("Active");

        // Command still succeeds — the ADO state transition completed
        result.ShouldBe(0);
        // Warning emitted to stderr about resync failure
        var stderrOutput = _stderr.ToString();
        stderrOutput.ShouldContain("resync failed");
        stderrOutput.ShouldContain("network timeout");
        // SaveAsync never called — FetchAsync for resync threw before save
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
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
