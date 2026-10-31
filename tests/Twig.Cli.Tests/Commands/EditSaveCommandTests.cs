using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
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
    private readonly IProviderWorkItemWriter _writer;
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
        _writer = AdoWriterFixture.Wrap(_adoService);
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _pendingChangeStore.GetChangesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());
        _editorLauncher = Substitute.For<IEditorLauncher>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    [Fact]
    public async Task Edit_OpensEditor_StagesChanges()
    {
        var item = CreateSeedItem(1, "Original Title");
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing #1 Original Title\nTitle: Updated Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
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

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
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
        var item = CreateSeedItem(1, "Title");
        item.ChangeState("New");
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing #1 Title\nTitle: Title\nState: Active\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
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

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
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

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_ValueContainingColon_ParsedCorrectly()
    {
        var item = CreateSeedItem(1, "Title");
        SetupActiveItem(item);

        // Value contains a colon — only the first colon is used as separator
        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Prefix: Suffix\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        await editCmd.ExecuteAsync();

        // Title changed from "Title" to "Prefix: Suffix"
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.Title", "Title", "Prefix: Suffix", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_SingleFieldMode_OpensEditorWithFieldContent()
    {
        var item = CreateSeedItem(1, "My Title");
        item.SetField("System.Description", "Old description");
        SetupActiveItem(item);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("# Editing System.Description for #1 My Title\nSystem.Description: New description\n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
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

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync("System.Description");

        result.ShouldBe(0);
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ── EditCommand push-on-write tests ──────────────────────────

    [Fact]
    public async Task Edit_NonSeed_PushesDirectly()
    {
        var item = CreateWorkItem(1, "Original Title");
        var remote = CreateWorkItem(1, "Original Title");
        var updated = CreateWorkItem(1, "Updated Title");
        SetupActiveItem(item);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, updated);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Updated Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Is<IReadOnlyList<FieldChange>>(c => c.Any(f => f.FieldName == "System.Title" && f.NewValue == "Updated Title")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(updated, Arg.Any<CancellationToken>());
        // Should NOT stage locally
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_Seed_StagesLocally()
    {
        var seed = CreateSeedItem(-1, "Seed Title");
        SetupActiveItem(seed);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: New Seed Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _pendingChangeStore.Received().AddChangeAsync(
            -1, "field", "System.Title", "Seed Title", "New Seed Title", Arg.Any<CancellationToken>());
        // Should NOT call ADO
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_NonSeed_PushFails_FallsBackToStagingWithWarning()
    {
        var item = CreateWorkItem(1, "Title");
        SetupActiveItem(item);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("network error"));

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Changed Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var (result, stderr) = await StderrCapture.RunAsync(() => editCmd.ExecuteAsync());

        result.ShouldBe(0);
        // Should fall back to staging
        await _pendingChangeStore.Received().AddChangeAsync(
            1, "field", "System.Title", "Title", "Changed Title", Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
        stderr.ShouldContain("staged locally");
    }

    [Fact]
    public async Task Edit_NonSeed_ResyncFails_WarnsDoesNotStage()
    {
        var item = CreateWorkItem(1, "Original");
        var remote = CreateWorkItem(1, "Original");
        SetupActiveItem(item);

        var fetchCallCount = 0;
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fetchCallCount++;
                if (fetchCallCount == 1)
                    return remote;
                throw new Exception("resync failed");
            });

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: New Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var (result, stderr) = await StderrCapture.RunAsync(() => editCmd.ExecuteAsync());

        // DD-8: Push succeeded, resync failed — return success
        result.ShouldBe(0);
        await _adoService.Received().PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Should NOT fall back to staging
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        stderr.ShouldContain("cache may be stale");
    }

    [Fact]
    public async Task Edit_NonSeed_ConflictAcceptRemote_ReturnsZeroNoPatch()
    {
        var item = CreateWorkItem(1, "Local Title");
        item.MarkSynced(5);
        item.SetField("System.Description", "Local desc");

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");

        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        // 0004 slice 3: a conflict needs BOTH sides off the base. These fixtures diverge on
        // Title, so stage the local Title edit or the divergence correctly auto-merges.
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title") });
        _consoleInput.ReadLine().Returns("r");

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Changed Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        // Should NOT patch — user accepted remote
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        // Remote should be saved
        await _workItemRepo.Received().SaveAsync(remote, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_NonSeed_ConflictAbort_ReturnsZeroNoPatch()
    {
        var item = CreateWorkItem(1, "Local Title");
        item.MarkSynced(5);
        item.SetField("System.Description", "Local desc");

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");

        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        // 0004 slice 3: a conflict needs BOTH sides off the base. These fixtures diverge on
        // Title, so stage the local Title edit or the divergence correctly auto-merges.
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title") });
        _consoleInput.ReadLine().Returns("a");

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Changed Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync();

        result.ShouldBe(0);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_NonSeed_ConflictJson_ReturnsOne()
    {
        var item = CreateWorkItem(1, "Local Title");
        item.MarkSynced(5);
        item.SetField("System.Description", "Local desc");

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(6);
        remote.SetField("System.Description", "Remote desc");

        SetupActiveItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        // 0004 slice 3: a conflict needs BOTH sides off the base. These fixtures diverge on
        // Title, so stage the local Title edit or the divergence correctly auto-merges.
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title") });

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Changed Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var result = await editCmd.ExecuteAsync(outputFormat: "json");

        result.ShouldBe(1);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Edit_NonSeed_AutoPushNotesFails_DoesNotStageFields()
    {
        var item = CreateWorkItem(1, "Original Title");
        var remote = CreateWorkItem(1, "Original Title");
        var updated = CreateWorkItem(1, "Updated Title");
        SetupActiveItem(item);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, updated);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // Set up a pending note that will fail to push
        var pendingNote = new PendingChangeRecord(1, "note", null, null, "Test note");
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { pendingNote });
        _adoService.AddCommentAsync(1, "Test note", Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("comment API failed"));

        _editorLauncher.LaunchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Title: Updated Title\nState: New\nAssignedTo: \n");

        var editCmd = new EditCommand(_resolver, _workItemRepo, _pendingChangeStore, _writer, _consoleInput, _editorLauncher, _formatterFactory, _hintEngine);
        var (result, stderr) = await StderrCapture.RunAsync(() => editCmd.ExecuteAsync());

        result.ShouldBe(0);
        // Fields were already pushed — must NOT stage locally (NFR-2)
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // PatchAsync was called (fields pushed successfully)
        await _adoService.Received().PatchAsync(1,
            Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        stderr.ShouldContain("Note push failed");
    }

    private void SetupActiveItem(WorkItem item)
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(item.Id);
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title)
    {
        var item = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        // Synced at revision 1, as anything read from ADO is; a provider write refuses revision 0 (#1178 Option A).
        item.MarkSynced(1);
        return item;
    }

    private static WorkItem CreateSeedItem(int id, string title)
    {
        return new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = title,
            State = "New",
            IsSeed = true,
            SeedCreatedAt = DateTimeOffset.UtcNow,
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
    }
}
