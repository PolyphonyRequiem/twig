using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Regression tests for PolyphonyRequiem/twig#251: a staged note must never be discarded
/// by <c>twig sync</c> without having first reached Azure DevOps.
/// <para>
/// The flusher used to push notes AFTER the field-level conflict flow. Every early exit in
/// that flow (<c>ConflictJsonEmitted</c>, <c>AcceptedRemote</c>, <c>Aborted</c>) skipped the
/// note push — and the <c>AcceptedRemote</c> branch additionally called
/// <c>ClearChangesAsync</c>, deleting the staged note row outright. Notes are additive ADO
/// comments and cannot conflict with field metadata, so they are now pushed first.
/// </para>
/// </summary>
public sealed class StagedNoteNotDroppedOnSyncTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IConsoleInput _consoleInput = Substitute.For<IConsoleInput>();
    private readonly OutputFormatterFactory _formatterFactory = new(new HumanOutputFormatter());
    private readonly StringWriter _stderr = new();

    private PendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory, _stderr);

    /// <summary>
    /// Stages a note plus a field change whose remote copy conflicts, so the conflict flow
    /// is guaranteed to run and take an early exit.
    /// </summary>
    private void StageNoteAndConflictingField(int id, string noteText)
    {
        var local = CreateWorkItem(id, title: "Local title");
        var remote = CreateWorkItem(id, title: "Remote title");

        // ConflictResolver.Resolve short-circuits to NoConflict when revisions match, so
        // the remote MUST be advanced or the conflict branch never runs and this whole
        // fixture silently degrades into the happy path.
        remote.MarkSynced(5);

        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(local);
        _adoService.FetchAsync(id, Arg.Any<CancellationToken>()).Returns(remote);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns([id]);
        _pendingChangeStore.GetChangesAsync(id, Arg.Any<CancellationToken>()).Returns(new[]
        {
            new PendingChangeRecord(id, "note", null, null, noteText),
            new PendingChangeRecord(id, "field", "System.Title", "Old title", "New title"),
        });
    }

    /// <summary>
    /// Guard for the fixture itself: proves the conflict branch actually fires. Without
    /// this, a setup regression (e.g. matching revisions) would make every test in this
    /// class pass vacuously against the very bug they exist to pin.
    /// </summary>
    [Fact]
    public void Fixture_ActuallyProducesAConflict()
    {
        var local = CreateWorkItem(1, "Local title");
        var remote = CreateWorkItem(1, "Remote title");
        remote.MarkSynced(5);

        // MergeResult is a `union`, so the concrete case must be pattern-matched out —
        // ShouldBeOfType<HasConflicts>() fails on the wrapper. This mirrors exactly how
        // ConflictResolutionFlow tests the result in production.
        (ConflictResolver.Resolve(local, remote) is HasConflicts)
            .ShouldBeTrue("the fixture must actually drive the conflict branch");
    }

    [Fact]
    public async Task Sync_ConflictAborted_StagedNoteStillPushed()
    {
        StageNoteAndConflictingField(1, "an 8KB resolution comment");
        _consoleInput.ReadLine().Returns("a"); // abort

        var result = await CreateFlusher().FlushAllAsync();

        await _adoService.Received(1).AddCommentAsync(1, "an 8KB resolution comment", Arg.Any<CancellationToken>());
        result.NotesStaged.ShouldBe(1);
        result.NotesPushed.ShouldBe(1, "an aborted FIELD conflict must not silently drop the note");
    }

    [Fact]
    public async Task Sync_ConflictAcceptRemote_StagedNoteStillPushedBeforeStateCleared()
    {
        StageNoteAndConflictingField(1, "note body");
        _consoleInput.ReadLine().Returns("r"); // accept remote — clears ALL pending rows

        var result = await CreateFlusher().FlushAllAsync();

        await _adoService.Received(1).AddCommentAsync(1, "note body", Arg.Any<CancellationToken>());
        result.NotesPushed.ShouldBe(1);

        Received.InOrder(() =>
        {
            _adoService.AddCommentAsync(1, "note body", Arg.Any<CancellationToken>());
            _pendingChangeStore.ClearChangesAsync(1, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// The note rows are cleared as soon as the comments land, so a later field failure
    /// cannot cause the same note to be pushed twice on the next sync.
    /// </summary>
    [Fact]
    public async Task Sync_NotePushed_NoteRowsClearedByType()
    {
        StageNoteAndConflictingField(1, "note body");
        _consoleInput.ReadLine().Returns("a");

        await CreateFlusher().FlushAllAsync();

        await _pendingChangeStore.Received(1)
            .ClearChangesByTypeAsync(1, "note", Arg.Any<CancellationToken>());
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
}
