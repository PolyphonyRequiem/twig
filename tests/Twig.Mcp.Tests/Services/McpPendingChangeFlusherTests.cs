using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Mcp.Services;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Services;

/// <summary>
/// Tests for <see cref="McpPendingChangeFlusher"/>.
/// Covers happy path, continue-on-failure (FR-7), notes-only bypass (FR-9),
/// conflict auto-retry via <c>ConflictRetryHelper</c>, and edge cases.
/// </summary>
public sealed class McpPendingChangeFlusherTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();

    private McpPendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore);

    // ═══════════════════════════════════════════════════════════════
    //  Happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_NoDirtyItems_ReturnsEmptySummary()
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task FlushAllAsync_SingleFieldChange_PushesAndResyncs()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var remote = new WorkItemBuilder(1, "Title").Build();
        remote.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Updated Title").Build();
        updated.MarkSynced(6);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, updated);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "Updated Title") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();

        await _adoService.Received(1).PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>());
        await _pendingChangeStore.Received(1).ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).SaveAsync(Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_MultipleItems_FlushesAll()
    {
        var item1 = new WorkItemBuilder(1, "Item 1").Build();
        item1.MarkSynced(3);
        var item2 = new WorkItemBuilder(2, "Item 2").Build();
        item2.MarkSynced(7);

        SetupDirtyIds(1, 2);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        var remote1 = new WorkItemBuilder(1, "Item 1").Build();
        remote1.MarkSynced(3);
        var remote2 = new WorkItemBuilder(2, "Item 2").Build();
        remote2.MarkSynced(7);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);
        _adoService.PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(10);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.State", "New", "Active") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(2);
        result.Failed.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  FR-9: Notes-only bypass
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_NotesOnly_BypassesConflictResolution()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Title").Build();
        updated.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(updated);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();

        await _adoService.Received(1).AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
        // Should NOT call PatchAsync or FetchAsync before notes (no field changes)
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_MixedFieldsAndNotes_FlushesAll()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var remote = new WorkItemBuilder(1, "Title").Build();
        remote.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Updated").Build();
        updated.MarkSynced(6);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, updated);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "field", "System.Title", "Title", "Updated"),
                new PendingChangeRecord(1, "note", null, null, "A note"),
            });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);

        await _adoService.Received(1).PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddCommentAsync(1, "A note", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Conflict auto-retry
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_ConflictOnFirstPatch_RetriesWithFreshRevision()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var staleRemote = new WorkItemBuilder(1, "Title").Build();
        staleRemote.MarkSynced(5);
        var freshRemote = new WorkItemBuilder(1, "Title").Build();
        freshRemote.MarkSynced(8);
        var afterPatch = new WorkItemBuilder(1, "Updated").Build();
        afterPatch.MarkSynced(9);

        SetupDirtyIds(1);
        SetupItem(item);

        // First FetchAsync returns stale remote (for initial revision check)
        // Second FetchAsync called by ConflictRetryHelper after conflict
        // Third FetchAsync for post-push resync
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(staleRemote, freshRemote, afterPatch);

        // First PatchAsync throws conflict, second succeeds
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Throws(new AdoConflictException(8));
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 8, Arg.Any<CancellationToken>())
            .Returns(9);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Title", "Updated") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(0);
    }

    [Fact]
    public async Task FlushAllAsync_PersistentConflict_RecordsFailure()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var remote = new WorkItemBuilder(1, "Title").Build();
        remote.MarkSynced(5);
        var freshRemote = new WorkItemBuilder(1, "Title").Build();
        freshRemote.MarkSynced(8);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(remote, freshRemote);

        // Both patches throw conflict — ConflictRetryHelper lets the second propagate
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new AdoConflictException(99));

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(1);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].WorkItemId.ShouldBe(1);
        result.Failures[0].Reason.ShouldContain("Concurrency conflict");
    }

    // ═══════════════════════════════════════════════════════════════
    //  FR-7: Continue on failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_FirstItemFails_ContinuesWithSecond()
    {
        var item1 = new WorkItemBuilder(1, "Item 1").Build();
        item1.MarkSynced(3);
        var item2 = new WorkItemBuilder(2, "Item 2").Build();
        item2.MarkSynced(7);

        SetupDirtyIds(1, 2);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        var remote1 = new WorkItemBuilder(1, "Item 1").Build();
        remote1.MarkSynced(3);
        var remote2 = new WorkItemBuilder(2, "Item 2").Build();
        remote2.MarkSynced(7);
        var updated2 = new WorkItemBuilder(2, "Item 2").Build();
        updated2.MarkSynced(8);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2, updated2);

        // Item 1: PatchAsync throws
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Boom"));
        // Item 2: PatchAsync succeeds
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), 7, Arg.Any<CancellationToken>())
            .Returns(8);

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.State", "New", "Active") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(1);
        result.Failures.Count.ShouldBe(1);
        result.Failures[0].WorkItemId.ShouldBe(1);
        result.Failures[0].Reason.ShouldBe("Boom");

        // Verify item 2 was flushed despite item 1 failing
        await _pendingChangeStore.Received(1).ClearChangesAsync(2, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task FlushAllAsync_ItemNotInCache_RecordsFailure()
    {
        SetupDirtyIds(99);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(1);
        result.Failures[0].WorkItemId.ShouldBe(99);
        result.Failures[0].Reason.ShouldContain("not found in cache");
    }

    [Fact]
    public async Task FlushAllAsync_NoPendingChanges_SkipsItem()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        SetupDirtyIds(1);
        SetupItem(item);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeRecord>());

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(0);
        result.Failures.ShouldBeEmpty();

        // Should not attempt to patch or fetch remote
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _adoService.DidNotReceive().FetchAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_NoteWithNullNewValue_Skipped()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Title").Build();
        updated.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(updated);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, null) });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        await _adoService.DidNotReceive().AddCommentAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_FieldChangeWithNullFieldName_Skipped()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Title").Build();
        updated.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(updated);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", null, "Old", "New") });

        var result = await CreateFlusher().FlushAllAsync();

        // Item should still be counted as flushed (the malformed change is skipped)
        result.Flushed.ShouldBe(1);
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_MultipleNotes_AllPostedAsComments()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Title").Build();
        updated.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(updated);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "note", null, null, "First note"),
                new PendingChangeRecord(1, "note", null, null, "Second note"),
                new PendingChangeRecord(1, "note", null, null, "Third note"),
            });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(0);

        await _adoService.Received(1).AddCommentAsync(1, "First note", Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddCommentAsync(1, "Second note", Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddCommentAsync(1, "Third note", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_AddCommentThrows_RecordsFailure()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.AddCommentAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Comment failed"));
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "note", null, null, "A note") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(1);
        result.Failures[0].WorkItemId.ShouldBe(1);
        result.Failures[0].Reason.ShouldBe("Comment failed");
    }

    [Fact]
    public async Task FlushAllAsync_ResyncFetchThrows_RecordsFailure()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var remote = new WorkItemBuilder(1, "Title").Build();
        remote.MarkSynced(5);

        SetupDirtyIds(1);
        SetupItem(item);

        // First FetchAsync (pre-patch) succeeds, second (resync) throws
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(remote), _ => throw new InvalidOperationException("Network error"));
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(1);
        result.Failures[0].Reason.ShouldBe("Network error");
    }

    [Fact]
    public async Task FlushAllAsync_MultipleFieldChanges_BatchedInSinglePatch()
    {
        var item = new WorkItemBuilder(1, "Title").Build();
        item.MarkSynced(5);
        var remote = new WorkItemBuilder(1, "Title").Build();
        remote.MarkSynced(5);
        var updated = new WorkItemBuilder(1, "Updated").Build();
        updated.MarkSynced(6);

        SetupDirtyIds(1);
        SetupItem(item);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote, updated);
        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), 5, Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new PendingChangeRecord(1, "field", "System.Title", "Title", "Updated"),
                new PendingChangeRecord(1, "field", "System.State", "New", "Active"),
                new PendingChangeRecord(1, "field", "System.AssignedTo", null, "Alice"),
            });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(1);
        result.Failed.ShouldBe(0);

        // PatchAsync should be called exactly once with all field changes batched
        await _adoService.Received(1).PatchAsync(
            1,
            Arg.Is<IReadOnlyList<FieldChange>>(fc => fc.Count == 3),
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_AllItemsFail_SummaryReflectsAllFailures()
    {
        var item1 = new WorkItemBuilder(1, "Item 1").Build();
        item1.MarkSynced(3);
        var item2 = new WorkItemBuilder(2, "Item 2").Build();
        item2.MarkSynced(7);

        SetupDirtyIds(1, 2);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(item1);
        _workItemRepo.GetByIdAsync(2, Arg.Any<CancellationToken>()).Returns(item2);

        var remote1 = new WorkItemBuilder(1, "Item 1").Build();
        remote1.MarkSynced(3);
        var remote2 = new WorkItemBuilder(2, "Item 2").Build();
        remote2.MarkSynced(7);

        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote1);
        _adoService.FetchAsync(2, Arg.Any<CancellationToken>()).Returns(remote2);

        _adoService.PatchAsync(1, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Error 1"));
        _adoService.PatchAsync(2, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Error 2"));

        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(1, "field", "System.Title", "Old", "New") });
        _pendingChangeStore.GetChangesAsync(2, Arg.Any<CancellationToken>())
            .Returns(new[] { new PendingChangeRecord(2, "field", "System.State", "New", "Active") });

        var result = await CreateFlusher().FlushAllAsync();

        result.Flushed.ShouldBe(0);
        result.Failed.ShouldBe(2);
        result.Failures.Count.ShouldBe(2);
        result.Failures[0].WorkItemId.ShouldBe(1);
        result.Failures[0].Reason.ShouldBe("Error 1");
        result.Failures[1].WorkItemId.ShouldBe(2);
        result.Failures[1].Reason.ShouldBe("Error 2");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private void SetupDirtyIds(params int[] ids)
    {
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(ids);
    }

    private void SetupItem(WorkItem item)
    {
        _workItemRepo.GetByIdAsync(item.Id, Arg.Any<CancellationToken>()).Returns(item);
    }
}
