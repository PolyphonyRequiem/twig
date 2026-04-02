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
/// Tests for <see cref="PendingChangeFlusher"/>: extracted per-item flush loop
/// with FlushResult/FlushItemFailure structured output.
/// Covers FR-7 (continue-on-failure), FR-9 (notes-only bypass), and cache resync.
/// </summary>
public sealed class PendingChangeFlusherTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly IConsoleInput _consoleInput;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StringWriter _stderr;

    public PendingChangeFlusherTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _stderr = new StringWriter();
    }

    private PendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, _formatterFactory, _stderr);

    // ═══════════════════════════════════════════════════════════════
    //  Happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAsync_SingleFieldChange_PushesAndResyncs()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.ItemsFlushed.ShouldBe(1);
        result.FieldChangesPushed.ShouldBe(1);
        result.NotesPushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();

        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_MultipleFieldChanges_CountsAllFields()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "field", "System.Title", "Old", "New"),
                new PendingChangeRecord(1, "field", "System.State", "Active", "Closed"),
            });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.FieldChangesPushed.ShouldBe(2);
        result.ItemsFlushed.ShouldBe(1);
    }

    [Fact]
    public async Task FlushAsync_EmptyList_ReturnsEmptyResult()
    {
        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([]);

        result.ItemsFlushed.ShouldBe(0);
        result.FieldChangesPushed.ShouldBe(0);
        result.NotesPushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushAsync_NoPendingChanges_SkipsItemGracefully()
    {
        var item = CreateWorkItem(1, "Title");
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.ItemsFlushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  FR-9: Notes-only bypass
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAsync_NotesOnly_BypassesConflictResolution()
    {
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.ItemsFlushed.ShouldBe(1);
        result.NotesPushed.ShouldBe(1);
        result.FieldChangesPushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();

        await _adoService.Received().AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task FlushAsync_NotesOnly_MultipleNotes_AllPushed()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "note", null, null, "First"),
                new PendingChangeRecord(1, "note", null, null, "Second"),
            });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.NotesPushed.ShouldBe(2);
        await _adoService.Received().AddCommentAsync(1, "First", Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(1, "Second", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_NotesOnly_NullNewValue_NoteSkipped()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "note", null, null, "Valid"),
                new PendingChangeRecord(1, "note", null, null, null),
            });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.NotesPushed.ShouldBe(1);
        await _adoService.Received(1).AddCommentAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_NotesOnly_OnlyFetchesOnceForResync()
    {
        var item = CreateWorkItem(1, "Title");
        var remote = CreateWorkItem(1, "Title");
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "Note") });

        var flusher = CreateFlusher();
        await flusher.FlushAsync([1]);

        // FetchAsync called exactly once: post-push cache resync only (no conflict fetch)
        await _adoService.Received(1).FetchAsync(1, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  FR-7: Continue-on-failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAsync_FirstItemFails_SecondItemStillFlushed()
    {
        var item1 = CreateWorkItem(1, "Failing");
        var item2 = CreateWorkItem(2, "Succeeding");
        var remote2 = CreateWorkItem(2, "Succeeding");

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("ADO unavailable"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1, 2]);

        result.ItemsFlushed.ShouldBe(1);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].ItemId.ShouldBe(1);
        result.Failures[0].Error.ShouldContain("ADO unavailable");
        _stderr.ToString().ShouldContain("#1");
    }

    [Fact]
    public async Task FlushAsync_AllItemsFail_ReturnsAllFailures()
    {
        var item1 = CreateWorkItem(1, "Fail");
        var item2 = CreateWorkItem(2, "Also Fail");

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Fail 1"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Fail 2"));

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1, 2]);

        result.ItemsFlushed.ShouldBe(0);
        result.Failures.Count.ShouldBe(2);
        result.Failures.ShouldContain(f => f.ItemId == 1);
        result.Failures.ShouldContain(f => f.ItemId == 2);
    }

    [Fact]
    public async Task FlushAsync_ItemNotFoundInCache_RecordsFailure()
    {
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([99]);

        result.ItemsFlushed.ShouldBe(0);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].ItemId.ShouldBe(99);
        result.Failures[0].Error.ShouldContain("#99");
    }

    [Fact]
    public async Task FlushAsync_NotesPushFails_RecordsFailureAndContinues()
    {
        var item1 = CreateWorkItem(1, "Notes Fail");
        var item2 = CreateWorkItem(2, "OK");
        var remote2 = CreateWorkItem(2, "OK");

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        _adoService.AddCommentAsync(1, "A note", Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("500 Internal Server Error"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1, 2]);

        result.ItemsFlushed.ShouldBe(1);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].ItemId.ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Conflict resolution paths
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAsync_ConflictJsonEmitted_RecordsFailure()
    {
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title") });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1], outputFormat: "json");

        result.ItemsFlushed.ShouldBe(0);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].ItemId.ShouldBe(1);
        result.Failures[0].Error.ShouldContain("conflict");
    }

    [Fact]
    public async Task FlushAsync_UserAbortsConflict_NoFailureRecorded()
    {
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title") });

        _consoleInput.ReadLine().Returns("a");

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        // Abort is a user choice, not an error
        result.ItemsFlushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushAsync_UserAcceptsRemote_NoFailureRecorded()
    {
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title") });

        _consoleInput.ReadLine().Returns("r");

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        // Accept-remote is a user choice, not an error
        result.ItemsFlushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushAsync_MixedChanges_UserKeepsLocal_BothFieldsAndNotesPushed()
    {
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        var postSaveRemote = CreateWorkItem(1, "New Title");
        postSaveRemote.MarkSynced(6);
        SetupItem(item);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(driftedRemote, postSaveRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "note", null, null, "A note"),
                new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title"),
            });

        _consoleInput.ReadLine().Returns("l");
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1]);

        result.ItemsFlushed.ShouldBe(1);
        result.FieldChangesPushed.ShouldBe(1);
        result.NotesPushed.ShouldBe(1);
        result.Failures.ShouldBeEmpty();

        await _adoService.Received().PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.Received().AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  FlushAllAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_DelegatesAllDirtyItems()
    {
        var item1 = CreateWorkItem(1, "A");
        var item2 = CreateWorkItem(2, "B");
        var remote1 = CreateWorkItem(1, "A");
        var remote2 = CreateWorkItem(2, "B");

        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1, 2 });
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "Note 1") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "note", null, null, "Note 2") });

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAllAsync();

        result.ItemsFlushed.ShouldBe(2);
        result.NotesPushed.ShouldBe(2);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushAllAsync_NoDirtyItems_ReturnsEmptyResult()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var flusher = CreateFlusher();
        var result = await flusher.FlushAllAsync();

        result.ItemsFlushed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAsync_ResyncFetchThrows_RecordsFailureAndContinues()
    {
        var item1 = CreateWorkItem(1, "Resync Fail");
        var item2 = CreateWorkItem(2, "OK");
        var remote2 = CreateWorkItem(2, "OK");

        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.Title", "Old", "New") });

        // Item 1: AddCommentAsync succeeds but post-push FetchAsync throws
        _adoService.AddCommentAsync(1, "A note", Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network timeout"));
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1, 2]);

        result.Failures.Count.ShouldBe(1);
        result.Failures[0].ItemId.ShouldBe(1);
        // Item 2 still flushed despite item 1 failure
        result.ItemsFlushed.ShouldBe(1);
        await _adoService.Received().PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_OutputFormatPassedToConflictResolution()
    {
        // When outputFormat is "json", conflict should emit JSON and record failure
        var item = CreateWorkItem(1, "Title");
        var driftedRemote = CreateDriftedRemote(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(driftedRemote);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "New Title") });

        var flusher = CreateFlusher();
        var result = await flusher.FlushAsync([1], "json");

        result.Failures.Count.ShouldBe(1);
        // No user prompt in JSON mode
        _consoleInput.DidNotReceive().ReadLine();
    }

    [Fact]
    public async Task FlushAsync_StderrDefaultsToConsoleError()
    {
        // Verify the null-coalescing default doesn't throw
        var flusher = new PendingChangeFlusher(
            _workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, _formatterFactory);
        var result = await flusher.FlushAsync([]);
        result.Failures.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void SetupItem(WorkItem item)
    {
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
