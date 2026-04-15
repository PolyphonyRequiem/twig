using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for the SyncGuard integration in RefreshCommand (ITEM-009):
/// dirty guard halts refresh when protected items would be overwritten.
/// </summary>
public class RefreshDirtyGuardTests : RefreshCommandTestBase
{
    private RefreshCommand CreateCommand(TextWriter? stderr = null) => CreateRefreshCommand(stderr);

    [Fact]
    public async Task CleanCache_RefreshesNormally()
    {
        // No dirty or pending items
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DirtyItem_NewerRemoteRevision_SkipsProtectedItem()
    {
        // Local item at revision 3, remote at revision 5 — should be skipped (not saved)
        var localItem = CreateWorkItem(1, "Local Item", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        // Per-item skip: protected items are skipped, refresh succeeds
        result.ShouldBe(0);
        var output = stderr.ToString();
        output.ShouldContain("#1");
        output.ShouldContain("local rev 3");
        output.ShouldContain("remote rev 5");

        // SaveBatchAsync should NOT have been called with the protected item
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DirtyItem_SameRevision_SkippedByProtectedCacheWriter()
    {
        // Local item at revision 5, remote also at revision 5 — previously saved,
        // now skipped by ProtectedCacheWriter (dirty items are always protected)
        var localItem = CreateWorkItem(1, "Local Item", revision: 5);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Protected item is skipped — SaveBatchAsync should not save item 1
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_OverridesDirtyGuard()
    {
        // Dirty item with newer remote revision, but --force bypasses
        var localItem = CreateWorkItem(1, "Local Item", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PendingOnlyItems_AreAlsoProtected()
    {
        // Item has no dirty flag on WorkItem but has pending changes in the store
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 }); // pending-only

        var localItem = CreateWorkItem(1, "Pending Item", revision: 3);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        // Per-item skip: protected item is skipped, refresh succeeds
        result.ShouldBe(0);
        stderr.ToString().ShouldContain("#1");

        // Protected item should not be saved
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveItemOutOfSprint_DirtyWithNewerRevision_SkipsProtectedItem()
    {
        // Active item 42 is outside the sprint scope, dirty with newer remote revision
        var localItem = CreateWorkItem(42, "Active Out-of-Sprint", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(localItem);

        // Sprint has item 1 (clean, no conflict)
        var sprintItem = CreateWorkItem(1, "Sprint Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });

        // Active item 42 is out-of-sprint → fetched individually
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var remoteActive = CreateWorkItem(42, "Active Out-of-Sprint Remote", revision: 7);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(remoteActive);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        // Per-item skip: protected items are skipped, refresh succeeds
        result.ShouldBe(0);
        var output = stderr.ToString();
        output.ShouldContain("#42");
        output.ShouldContain("local rev 3");
        output.ShouldContain("remote rev 7");

        // Sprint items (clean) should be saved; active item 42 (protected) should be skipped
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
        // Protected active item should not be saved
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 42), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildrenOutOfSprint_DirtyWithNewerRevision_SkipsProtectedChild()
    {
        // Child item 99 is dirty with newer remote revision
        var localChild = CreateWorkItem(99, "Dirty Child", revision: 2);
        localChild.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localChild });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(localChild);

        // Sprint has item 1 (clean)
        var sprintItem = CreateWorkItem(1, "Sprint Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });

        // Active item 10 is outside the sprint scope (WIQL returns [1], so 10 is fetched individually)
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        var remoteActive = CreateWorkItem(10, "Active In Sprint", revision: 5);
        _adoService.FetchAsync(10, Arg.Any<CancellationToken>()).Returns(remoteActive);
        var remoteChild = CreateWorkItem(99, "Dirty Child Remote", revision: 6);
        _adoService.FetchChildrenAsync(10, Arg.Any<CancellationToken>())
            .Returns(new[] { remoteChild });

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        // Per-item skip: protected items are skipped, refresh succeeds
        result.ShouldBe(0);
        var output = stderr.ToString();
        output.ShouldContain("#99");
        output.ShouldContain("local rev 2");
        output.ShouldContain("remote rev 6");

        // Sprint items (clean) should be saved
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
        // Active item (clean) should be saved
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 10), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActiveItemOutOfSprint_Force_SavesNormally()
    {
        // Active item 42 is outside sprint, dirty with newer remote — but --force bypasses
        var localItem = CreateWorkItem(42, "Active Out-of-Sprint", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var sprintItem = CreateWorkItem(1, "Sprint Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { sprintItem });

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        var remoteActive = CreateWorkItem(42, "Active Remote", revision: 7);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(remoteActive);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 42), Arg.Any<CancellationToken>());
    }

    // ── E5-T7: New tests for RefreshCommand behavioral change ──────────

    [Fact]
    public async Task ProtectedItem_NoRevisionConflict_IsNowSkipped()
    {
        // Protected item with same revision (no revision conflict) is now skipped
        // by ProtectedCacheWriter. Previously this was saved during non-force refresh.
        var localItem = CreateWorkItem(1, "Local Item", revision: 5);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        // Remote has same revision — no revision conflict
        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 5);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
        // Protected item should be skipped — no SaveBatchAsync with this item
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProtectedItem_WithRevisionConflict_SkippedAndWarningShown()
    {
        // Protected item with newer remote revision — skipped with informational warning
        var localItem = CreateWorkItem(1, "Local Item", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 7);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var stderr = new StringWriter();
        var cmd = CreateCommand(stderr);
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0); // No longer blocks — informational warning only
        var output = stderr.ToString();
        output.ShouldContain("Warning");
        output.ShouldContain("#1");
        output.ShouldContain("local rev 3");
        output.ShouldContain("remote rev 7");
        output.ShouldContain("twig sync");

        // Protected item not saved
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Force_SavesAllItems_IncludingProtected()
    {
        // --force saves everything including protected items
        var localItem = CreateWorkItem(1, "Local Item", revision: 3);
        localItem.SetDirty();
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { localItem });
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(localItem);

        var remoteItem = CreateWorkItem(1, "Remote Item", revision: 7);
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteItem });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync(force: true);

        result.ShouldBe(0);
        // --force bypasses ProtectedCacheWriter, saves all via raw SaveBatchAsync
        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
    }

    private static WorkItem CreateWorkItem(int id, string title, int revision = 0)
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
        if (revision > 0)
            item.MarkSynced(revision);
        return item;
    }
}
