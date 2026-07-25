using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Characterization tests for the staged-note flush path that PolyphonyRequiem/twig#252's
/// counters depend on, and which PolyphonyRequiem/twig#251 reports as lossy.
/// <para>
/// #251 is NOT fixed here — these tests pin the behaviour the #252 counters rely on, so a
/// future change to the note path cannot silently make <c>notesDropped</c> lie. If someone
/// fixes #251 such that sync flushes or preserves staged notes, the
/// <see cref="Sync_StagedNote_IsPushed_NotDropped"/> expectation is the one to revisit.
/// </para>
/// </summary>
public sealed class StagedNoteFlushCharacterizationTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IConsoleInput _consoleInput = Substitute.For<IConsoleInput>();
    private readonly OutputFormatterFactory _formatterFactory = new(new HumanOutputFormatter());
    private readonly StringWriter _stderr = new();

    private PendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory, _stderr);

    private void StageNote(int id, string text)
    {
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(id));
        _adoService.FetchAsync(id, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(id));
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns([id]);
        _pendingChangeStore.GetChangesAsync(id, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(id, "note", null, null, text) });
    }

    /// <summary>
    /// The flush loop DOES reach a staged note and push it — the note is not dropped by the
    /// flusher itself. This is why #252's counters can report honestly: staged and pushed
    /// are both observable at the point the counters are built.
    /// </summary>
    [Fact]
    public async Task Sync_StagedNote_IsPushed_NotDropped()
    {
        StageNote(1, "an 8KB resolution comment");

        var result = await CreateFlusher().FlushAllAsync();

        result.NotesStaged.ShouldBe(1);
        result.NotesPushed.ShouldBe(1);
        await _adoService.Received(1).AddCommentAsync(1, "an 8KB resolution comment", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pending state is cleared only AFTER a successful push. A note is therefore never
    /// discarded by the flusher without having reached ADO first.
    /// </summary>
    [Fact]
    public async Task Sync_StagedNote_PendingStateClearedOnlyAfterSuccessfulPush()
    {
        StageNote(1, "note body");

        await CreateFlusher().FlushAllAsync();

        Received.InOrder(() =>
        {
            _adoService.AddCommentAsync(1, "note body", Arg.Any<CancellationToken>());
            _pendingChangeStore.ClearChangesAsync(1, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// The loss-bearing case: when the push throws, the staged note must NOT be cleared,
    /// and the shortfall must be visible as staged &gt; pushed. If this ever regresses to
    /// clearing on failure, that is real data loss and #252's counters are the alarm.
    /// </summary>
    [Fact]
    public async Task Sync_StagedNote_PushFails_StateNotClearedAndShortfallVisible()
    {
        StageNote(1, "note body");
        _adoService.AddCommentAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO down"));

        var result = await CreateFlusher().FlushAllAsync();

        result.NotesStaged.ShouldBe(1);
        result.NotesPushed.ShouldBe(0);
        (result.NotesStaged - result.NotesPushed).ShouldBe(1, "the drop must be visible to #252's counters");
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(1, Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id) => new()
    {
        Id = id,
        Type = WorkItemType.Task,
        Title = "Title",
        State = "New",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };
}
