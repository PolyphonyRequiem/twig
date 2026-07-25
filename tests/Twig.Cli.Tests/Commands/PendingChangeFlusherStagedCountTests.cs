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
/// Regression tests for PolyphonyRequiem/twig#252 at the flusher level: the flush loop
/// must record what was <em>staged</em> (read out of the pending-change store) separately
/// from what was actually <em>pushed</em> to ADO, so callers can distinguish
/// "nothing was pending" from "was pending and never went out".
/// </summary>
public sealed class PendingChangeFlusherStagedCountTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IConsoleInput _consoleInput = Substitute.For<IConsoleInput>();
    private readonly OutputFormatterFactory _formatterFactory = new(new HumanOutputFormatter());
    private readonly StringWriter _stderr = new();

    private PendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory, _stderr);

    [Fact]
    public async Task NothingPending_StagedCountsAreZero()
    {
        var item = CreateWorkItem(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await CreateFlusher().FlushAsync([1]);

        result.FieldChangesStaged.ShouldBe(0);
        result.NotesStaged.ShouldBe(0);
        result.FieldChangesPushed.ShouldBe(0);
        result.NotesPushed.ShouldBe(0);
    }

    [Fact]
    public async Task PendingNote_PushedSuccessfully_StagedEqualsPushed()
    {
        var item = CreateWorkItem(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(1));
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "hello") });

        var result = await CreateFlusher().FlushAsync([1]);

        result.NotesStaged.ShouldBe(1);
        result.NotesPushed.ShouldBe(1);
        (result.NotesStaged - result.NotesPushed).ShouldBe(0);
    }

    [Fact]
    public async Task PendingNote_PushFails_StagedExceedsPushed()
    {
        // The lossy shape: the note was read out of the store, but AddCommentAsync blew up,
        // so nothing reached ADO. `NotesPushed` alone would report a bare 0 here — identical
        // to the benign "nothing pending" case. `NotesStaged` is what disambiguates it.
        var item = CreateWorkItem(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "hello") });
        _adoService.AddCommentAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO down"));

        var result = await CreateFlusher().FlushAsync([1]);

        result.NotesStaged.ShouldBe(1);
        result.NotesPushed.ShouldBe(0);
        result.Failures.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PendingFieldChanges_AreCountedAsStaged()
    {
        var item = CreateWorkItem(1);
        var remote = CreateWorkItem(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "field", "System.Title", "Old", "New"),
                new PendingChangeRecord(1, "field", "System.State", "New", "Active"),
            });

        var result = await CreateFlusher().FlushAsync([1]);

        result.FieldChangesStaged.ShouldBe(2);
        result.FieldChangesPushed.ShouldBe(2);
        result.NotesStaged.ShouldBe(0);
    }

    [Fact]
    public async Task StagedCountsAccumulateAcrossItems()
    {
        foreach (var id in new[] { 1, 2 })
        {
            _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(id));
            _adoService.FetchAsync(id, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(id));
            _pendingChangeStore.GetChangesAsync(id, Arg.Any<CancellationToken>())
                .Returns(new[] { new PendingChangeRecord(id, "note", null, null, $"note-{id}") });
        }

        var result = await CreateFlusher().FlushAsync([1, 2]);

        result.NotesStaged.ShouldBe(2);
        result.NotesPushed.ShouldBe(2);
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
