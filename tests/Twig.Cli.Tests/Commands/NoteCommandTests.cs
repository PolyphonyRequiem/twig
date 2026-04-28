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
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class NoteCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IAdoWorkItemService _adoService;
    private readonly IEditorLauncher _editorLauncher;
    private readonly NoteCommand _cmd;

    public NoteCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        _cmd = new NoteCommand(resolver, _workItemRepo, _pendingChangeStore, _adoService,
            _editorLauncher, formatterFactory, hintEngine);
    }

    [Fact]
    public async Task Note_InlineText_StoresPending()
    {
        var item = CreateWorkItem(1, "Test Item");
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("This is a note");

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "note", null, null, "This is a note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_EditorLaunch_StoresResult()
    {
        var item = CreateWorkItem(1, "Test Item");
        SetupActiveItem(item);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Edited note content");

        var result = await _cmd.ExecuteAsync(null);

        result.ShouldBe(0);
        await _editorLauncher.Received().LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "note", null, null, "Edited note content", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_EditorAbort_ReturnsZeroNothingStored()
    {
        var item = CreateWorkItem(1, "Test Item");
        SetupActiveItem(item);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _cmd.ExecuteAsync(null);

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await _cmd.ExecuteAsync("some note");

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Note_PendingNoteStored_MarksDirty()
    {
        var item = CreateWorkItem(1, "Test Item");
        SetupActiveItem(item);

        await _cmd.ExecuteAsync("A note");

        // SaveAsync should be called with the dirty item
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.IsDirty), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_NonSeed_PushesImmediately()
    {
        var item = CreateWorkItem(42, "Published Item", isSeed: false);
        SetupActiveItem(item);

        var serverItem = CreateWorkItem(42, "Published Item", isSeed: false);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(serverItem);

        var result = await _cmd.ExecuteAsync("Fix applied");

        result.ShouldBe(0);
        await _adoService.Received(1).AddCommentAsync(42, "Fix applied", Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesByTypeAsync(42, "note", Arg.Any<CancellationToken>());
        await _adoService.Received(1).FetchAsync(42, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(serverItem, Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_Seed_StagesLocally()
    {
        var item = CreateWorkItem(7, "Seed Item", isSeed: true);
        SetupActiveItem(item);

        var result = await _cmd.ExecuteAsync("Draft note");

        result.ShouldBe(0);
        await _adoService.DidNotReceive().AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).AddChangeAsync(
            7, "note", null, null, "Draft note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_PushFailure_FallsBackToStagingWithWarning()
    {
        var item = CreateWorkItem(10, "Remote Item", isSeed: false);
        SetupActiveItem(item);
        _adoService.AddCommentAsync(10, "Offline note", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("Network error")));

        var (result, stderr) = await StderrCapture.RunAsync(() => _cmd.ExecuteAsync("Offline note"));

        result.ShouldBe(0);
        await _pendingChangeStore.Received(1).AddChangeAsync(
            10, "note", null, null, "Offline note", Arg.Any<CancellationToken>());
        stderr.ShouldContain("staged locally");
    }

    [Fact]
    public async Task Note_ResyncFailure_WarnsButDoesNotStage()
    {
        var item = CreateWorkItem(20, "Pushed Item", isSeed: false);
        SetupActiveItem(item);
        _adoService.FetchAsync(20, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Timeout"));

        var (result, stderr) = await StderrCapture.RunAsync(() => _cmd.ExecuteAsync("Resync fail note"));

        result.ShouldBe(0);
        await _adoService.Received(1).AddCommentAsync(20, "Resync fail note", Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        stderr.ShouldContain("cache may be stale");
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title, bool isSeed = true)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IsSeed = isSeed,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }

    [Fact]
    public async Task Note_WithExplicitId_AddsNoteToSpecificItem()
    {
        var item = CreateWorkItem(42, "Specific Item", isSeed: false);
        // Explicit ID does NOT require active context
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        var serverItem = CreateWorkItem(42, "Specific Item", isSeed: false);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(serverItem);

        var result = await _cmd.ExecuteAsync("Note text", id: 42);

        result.ShouldBe(0);
        await _adoService.Received(1).AddCommentAsync(42, "Note text", Arg.Any<CancellationToken>());
        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Note_WithExplicitId_NotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(99, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<WorkItem>(new HttpRequestException("Not found")));

        var result = await _cmd.ExecuteAsync("Some note", id: 99);

        result.ShouldBe(1);
    }
}
