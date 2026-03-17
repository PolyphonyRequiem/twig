using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class NoteCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly NoteCommand _cmd;

    public NoteCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();

        var adoService = Substitute.For<IAdoWorkItemService>();
        var formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });

        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, adoService);
        _cmd = new NoteCommand(resolver, _workItemRepo, _pendingChangeStore, _editorLauncher,
            formatterFactory, hintEngine);
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

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
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
