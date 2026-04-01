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
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class EditSaveCommandTests
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IEditorLauncher _editorLauncher;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;
    private readonly ActiveItemResolver _resolver;

    public EditSaveCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    [Fact]
    public async Task Edit_OpensEditor_StagesChanges()
    {
        var item = CreateWorkItem(1, "Original Title");
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing #1 Original Title\nTitle: Updated Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.Title", "Original Title", "Updated Title", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_EditorAbort_NoChanges()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_PushesPendingFields()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var fieldChange = new PendingChangeRecord(1, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { fieldChange });

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_NoPending_NoOp()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_PushesNotes()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        var note = new PendingChangeRecord(1, "note", null, null, "A note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { note });

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
    }

    // ── SaveCommand conflict retry integration tests ──────────────────

    [Fact]
    public async Task Save_PatchConflictOnFirstAttempt_RetriesAndSucceeds()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });

        // Pre-patch fetch returns rev 3
        var remote = CreateWorkItem(1, "Title");
        remote.MarkSynced(3);

        // After conflict, helper re-fetches → rev 5
        var freshRemote = CreateWorkItem(1, "Title");
        freshRemote.MarkSynced(5);

        // Post-save cache refresh
        var updatedRemote = CreateWorkItem(1, "Title");
        updatedRemote.MarkSynced(6);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshRemote, updatedRemote);

        var fieldChange = new PendingChangeRecord(1, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { fieldChange });

        // First PatchAsync with rev 3 → conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));

        // Retry PatchAsync with fresh rev 5 → success
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // Verify retry path was followed
        await _adoService.Received(1).PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>());
        await _adoService.Received(1).PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_PatchConflictOnBothAttempts_LogsErrorAndReturnsFailure()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });

        var remote = CreateWorkItem(1, "Title");
        remote.MarkSynced(3);

        var freshRemote = CreateWorkItem(1, "Title");
        freshRemote.MarkSynced(5);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshRemote);

        var fieldChange = new PendingChangeRecord(1, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { fieldChange });

        // Both attempts conflict
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(5));
        _adoService
            .PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(7));

        var stderr = new StringWriter();
        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory, stderr: stderr);

        var result = await saveCmd.ExecuteAsync(all: true);

        // FR-7: Exception is caught, logged, and loop continues (returns error code)
        result.ShouldBe(1);
        stderr.ToString().ShouldContain("#1");

        // Pending changes should NOT be cleared on failure
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(1, Arg.Any<CancellationToken>());
    }

    // ── EditCommand field parser tests ─────────────────────────────

    [Fact]
    public async Task Edit_CommentLines_AreIgnored()
    {
        var item = CreateWorkItem(1, "Original Title");
        SetupActiveItem(item);

        // Comment lines and blank lines must not produce changes
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# This is a comment\n# Another comment\nTitle: Original Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        // No actual field change — Title unchanged
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), "System.Title",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_StateChange_StagesChange()
    {
        var item = CreateWorkItem(1, "Title");
        item.ChangeState("New");
        item.ApplyCommands();
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing #1 Title\nTitle: Title\nState: Active\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.State", "New", "Active", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_LineWithNoColon_IsIgnored()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);

        // Line with no colon should be silently skipped
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Title\nThisLineHasNoColon\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        // No changes detected (no actual value modifications)
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Edit_UnchangedValues_NoChangeStaged()
    {
        var item = CreateWorkItem(1, "My Title");
        SetupActiveItem(item);

        // All values identical to current item — no changes should be staged
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: My Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_ValueContainingColon_ParsedCorrectly()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);

        // Value contains a colon — only the first colon is used as separator
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Prefix: Suffix\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        await editCmd.ExecuteAsync();

        // Title changed from "Title" to "Prefix: Suffix"
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.Title", "Title", "Prefix: Suffix", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_SingleFieldMode_OpensEditorWithFieldContent()
    {
        var item = CreateWorkItem(1, "My Title");
        item.SetField("System.Description", "Old description");
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing System.Description for #1 My Title\nSystem.Description: New description\n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync("System.Description");

        result.ShouldBe(0);
        // Field change should be staged
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.Description",
            Arg.Any<string?>(), "New description", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_SingleFieldMode_UnchangedValue_NoChangeStaged()
    {
        var item = CreateWorkItem(1, "My Title");
        item.SetField("System.Description", "Same description");
        SetupActiveItem(item);

        // Editor returns the same value
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing System.Description for #1 My Title\nSystem.Description: Same description\n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync("System.Description");

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── SaveCommand multi-item tests ──────────────────────────────

    [Fact]
    public async Task Save_NonActiveItem_PushesSuccessfully()
    {
        // Active item is 1, but dirty item is 2 (non-active)
        var activeItem = CreateWorkItem(1, "Active Item");
        var dirtyItem = CreateWorkItem(2, "Non-Active Item");
        var remote2 = CreateWorkItem(2, "Non-Active Item");
        SetupActiveItem(activeItem);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 2 });
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(dirtyItem);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var fieldChange = new PendingChangeRecord(2, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { fieldChange });

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(2, Arg.Any<CancellationToken>());
        // Active item should NOT be touched
        await _adoService.DidNotReceive().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_MultipleDirtyItems_PushesAll()
    {
        var item1 = CreateWorkItem(1, "Item One");
        var item2 = CreateWorkItem(2, "Item Two");
        var remote1 = CreateWorkItem(1, "Item One");
        var remote2 = CreateWorkItem(2, "Item Two");
        SetupActiveItem(item1);

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var change1 = new PendingChangeRecord(1, "field", "System.Title", "Old1", "New1");
        var change2 = new PendingChangeRecord(2, "field", "System.State", "New", "Active");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { change1 });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { change2 });

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // Both items should be pushed
        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_DirtyItemNotInCache_PrintsErrorAndReturnsOne()
    {
        // Dirty item 99 is not found in the work item cache
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 99 });
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        result.ShouldBe(1);
        await _adoService.DidNotReceive().PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Save_OneItemFails_OtherSucceeds_ReturnsOne()
    {
        // Item 1 is not in cache (triggers hadErrors), item 2 pushes successfully
        var item2 = CreateWorkItem(2, "Item Two");
        var remote2 = CreateWorkItem(2, "Item Two");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var change2 = new PendingChangeRecord(2, "field", "System.Title", "Old", "New");
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { change2 });

        var saveCmd = new SaveCommand(_workItemRepo, _adoService, _pendingChangeStore,
            _resolver, _consoleInput, _formatterFactory);
        var result = await saveCmd.ExecuteAsync(all: true);

        // Overall result should be 1 (hadErrors) because item 1 failed
        result.ShouldBe(1);
        // Item 2 should still be pushed successfully
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(2, Arg.Any<CancellationToken>());
        // Item 1 should not have been patched
        await _adoService.DidNotReceive().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
