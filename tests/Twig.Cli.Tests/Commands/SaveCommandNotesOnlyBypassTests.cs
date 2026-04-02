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
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for SaveCommand notes-only bypass (FR-9): when pending changes consist exclusively
/// of notes (no field edits), conflict resolution is bypassed and notes are appended directly
/// to ADO via AddCommentAsync. Mirrors SaveCommandScopingTests structure.
/// </summary>
public sealed class SaveCommandNotesOnlyBypassTests : SaveCommandTestBase
{
    public SaveCommandNotesOnlyBypassTests() { }

    // ═══════════════════════════════════════════════════════════════
    //  Notes-only bypass path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task NotesOnly_BypassesConflictDetection_AppendsNotesWithoutError()
    {
        // Arrange: item with only a note pending, remote has drifted metadata
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });

        // Act
        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        // Assert: bypass succeeded — no conflict prompt, no patch, note pushed
        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task NotesOnly_MultipleNotes_EachWrittenToAdo()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        var notes = new[]
        {
            new PendingChangeRecord(1, "note", null, null, "First note"),
            new PendingChangeRecord(1, "note", null, null, "Second note"),
            new PendingChangeRecord(1, "note", null, null, "Third note"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(notes);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(1, "First note", Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(1, "Second note", Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(1, "Third note", Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task NotesOnly_ClearsChangesAndResyncsCacheAfterPush()
    {
        var item = CreateWorkItem(1, "Title");
        var resyncedRemote = CreateWorkItem(1, "Title");
        resyncedRemote.MarkSynced(7);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(resyncedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "Done") });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotesOnly_NoRemoteFetchForConflictComparison()
    {
        // Notes-only items skip FetchAsync for conflict resolution;
        // FetchAsync is called exactly once for post-push cache resync.
        var item = CreateWorkItem(1, "Title");
        var resyncRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(resyncRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "Note text") });

        var cmd = CreateCommand();
        await cmd.ExecuteAsync(all: true);

        // FetchAsync called exactly once (post-push cache resync), not for conflict check
        await _adoService.Received(1).FetchAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotesOnly_NullNewValue_NoteSkipped()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        // One valid note and one with null NewValue — only the valid one should be pushed
        var changes = new[]
        {
            new PendingChangeRecord(1, "note", null, null, "Valid note"),
            new PendingChangeRecord(1, "note", null, null, null),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(changes);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        await _adoService.Received(1).AddCommentAsync(1, "Valid note", Arg.Any<CancellationToken>());
        // Null note should not generate an AddCommentAsync call
        await _adoService.Received(1).AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotesOnly_ViaExplicitTargetId_BypassesConflictDetection()
    {
        // Bypass should work regardless of save scoping mode
        var item = CreateWorkItem(42, "Item");
        var driftedRemote = CreateDriftedRemote(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(42, "note", null, null, "Targeted note") });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(targetId: 42);

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(42, "Targeted note", Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task NotesOnly_ViaActiveWorkTree_BypassesConflictDetection()
    {
        // Active work tree mode: active item has only notes pending
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10 });

        var item = CreateWorkItem(10, "Active Item");
        var driftedRemote = CreateDriftedRemote(10);
        _workItemRepo.GetByIdAsync(10, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(10, "note", null, null, "Active note") });

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(); // default = active work tree

        result.ShouldBe(0);
        await _adoService.Received().AddCommentAsync(10, "Active note", Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Mixed changes — conflict detection fires
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task MixedChanges_WithConflict_ConflictDetectionFires_UserAborts_NotesNotPushed()
    {
        // Notes + field changes with metadata drift → conflict prompt shown
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        var changes = new[]
        {
            new PendingChangeRecord(1, "note", null, null, "A note"),
            new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(changes);

        // User aborts at conflict prompt
        _consoleInput.ReadLine().Returns("a");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0); // Abort is not an error
        // Conflict prompt WAS shown
        _consoleInput.Received().ReadLine();
        // Notes NOT pushed because abort continues to next item
        await _adoService.DidNotReceive().AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixedChanges_JsonConflict_ReturnsErrorAndBlocksNotes()
    {
        // In JSON output mode, conflict emits JSON and returns error code 1
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        var changes = new[]
        {
            new PendingChangeRecord(1, "note", null, null, "A note"),
            new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(changes);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true, outputFormat: "json");

        result.ShouldBe(1);
        // Notes should NOT be pushed — conflict JSON emitted, loop continued
        await _adoService.DidNotReceive().AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MixedChanges_UserKeepsLocal_BothFieldsAndNotesPushed()
    {
        // When user resolves conflict by keeping local, both fields and notes are pushed
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);

        // First FetchAsync: conflict comparison; second: cache resync
        var postSaveRemote = CreateWorkItem(1, "New Title");
        postSaveRemote.MarkSynced(6);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(driftedRemote, postSaveRemote);

        var changes = new[]
        {
            new PendingChangeRecord(1, "note", null, null, "A note"),
            new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title"),
        };
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>()).Returns(changes);

        // User keeps local at conflict prompt
        _consoleInput.ReadLine().Returns("l");
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        // Both field patch AND note comment should have been pushed
        await _adoService.Received().PatchAsync(
            1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FieldChangesOnly_WithConflict_ConflictDetectionFires()
    {
        // Sanity check: field-only changes go through normal conflict path
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupDirtyItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title") });

        _consoleInput.ReadLine().Returns("a");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(all: true);

        result.ShouldBe(0);
        _consoleInput.Received().ReadLine();
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Sets up a dirty item via --all save mode.</summary>
    private void SetupDirtyItem(WorkItem item)
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { item.Id });
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }

    private static WorkItem CreateWorkItem(int id, string title) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = title,
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    /// <summary>
    /// Creates a remote work item with drifted metadata (different IterationPath + higher revision)
    /// to trigger conflict detection when field changes are present.
    /// </summary>
    private static WorkItem CreateDriftedRemote(int id)
    {
        var remote = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Task,
            Title = "Title",
            State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 2").Value,
            AreaPath = AreaPath.Parse("Project").Value,
        };
        remote.MarkSynced(5);
        return remote;
    }
}
