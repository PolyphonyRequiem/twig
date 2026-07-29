using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Wayfinder 0004 slice 5 — site 3: <see cref="PendingChangeFlusher"/>'s post-push resync.
/// </summary>
/// <remarks>
/// <para>
/// The resync used to read:
/// </para>
/// <code>
/// await pendingChangeStore.ClearChangesAsync(item.Id, ct);
/// var updated = await adoService.FetchAsync(item.Id, ct);
/// await workItemRepo.SaveAsync(updated, ct);
/// </code>
/// <para>
/// — a direct write with no protection, sitting five lines below a field-change path that
/// already went through <c>ConflictResolutionFlow.ResolveAsync</c>. Anything still staged when
/// control reached it had NOT been pushed (a row of an unrecognised change type, or an edit
/// staged concurrently with the flush), and the clear-then-overwrite destroyed it without a
/// prompt — the silent coercion 0003 §4 forbids.
/// </para>
/// <para>
/// The resync now goes THROUGH the resolver. The trap these fixtures must avoid: under
/// three-way merge a conflict needs BOTH sides off the merge base, and revision equality
/// short-circuits to <c>NoConflict</c> — a fresh <see cref="WorkItem"/> is <c>Revision = 0</c> on
/// both sides. <see cref="AssertConflictPathReachable"/> asserts both preconditions so a future
/// setup regression cannot hollow this file out into a happy-path pass.
/// </para>
/// </remarks>
public sealed class PendingChangeFlusherResyncBypassTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly IConsoleInput _consoleInput = Substitute.For<IConsoleInput>();
    private readonly OutputFormatterFactory _formatterFactory =
        new(new HumanOutputFormatter());
    private readonly StringWriter _stderr = new();

    private PendingChangeFlusher CreateFlusher() =>
        new(_workItemRepo, _adoService, _pendingChangeStore,
            _consoleInput, _formatterFactory, _stderr);

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
    /// Asserts the two preconditions that make a three-way conflict reachable. Without both,
    /// <c>ThreeWayMerge.Resolve</c> returns before the branch under test ever runs and the test
    /// passes for the wrong reason.
    /// </summary>
    private static void AssertConflictPathReachable(
        WorkItem local, WorkItem remote, PendingChangeRecord staged, string remoteValue)
    {
        local.Revision.ShouldNotBe(remote.Revision,
            "revision equality short-circuits to NoConflict before any field is compared");
        staged.NewValue.ShouldNotBe(staged.OldValue,
            "the local side must be off the merge base or only remote moved (auto-merge)");
        remoteValue.ShouldNotBe(staged.OldValue,
            "the remote side must be off the merge base or only local moved (keep local)");
        remoteValue.ShouldNotBe(staged.NewValue,
            "convergent edits are not conflicts — both sides must disagree");
    }

    /// <summary>
    /// A staged edit that exists at resync time must not be clobbered. The user is asked; they
    /// abort; the cache is left alone and the pending rows survive.
    /// </summary>
    /// <remarks>
    /// <b>The fixture matters more than the assertion here.</b> The obvious setup — stage a field
    /// edit up front — proves nothing: the push loop routes ANY non-note row carrying a
    /// <c>FieldName</c> into <c>fieldChanges</c>, so the pre-existing field-change path resolves
    /// it and takes an early <c>continue</c> before the resync is ever reached. A first draft of
    /// this test passed against the unfixed flusher for exactly that reason.
    /// <para>
    /// The state that actually reaches the old bypass is a row staged <i>after</i> the flush loop
    /// read the pending store — a concurrent edit — so the push has nothing to resolve and the
    /// resync's blind <c>ClearChangesAsync</c> + <c>SaveAsync</c> destroys it. That is modelled by
    /// sequencing the substitute: notes on the first read, the concurrent field edit on the second.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Resync_WithEditStagedDuringTheFlush_DoesNotOverwriteTheCacheWithoutAsking()
    {
        var local = CreateWorkItem(1, "Local Title");
        local.MarkSynced(3);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(local);

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(9);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        // Staged concurrently with the flush — present on the resync's read, absent on the first.
        var concurrent = new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title");

        var reads = 0;
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads++;
                return reads == 1
                    // Notes only: pushed and cleared, so the field-change path never runs and
                    // control genuinely reaches the resync.
                    ? Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                        new[] { new PendingChangeRecord(1, "note", null, null, "a note") })
                    : Task.FromResult<IReadOnlyList<PendingChangeRecord>>(new[] { concurrent });
            });

        AssertConflictPathReachable(local, remote, concurrent, remote.Title);

        _consoleInput.ReadLine().Returns("a");   // abort

        var result = await CreateFlusher().FlushAsync([1]);

        reads.ShouldBeGreaterThan(1,
            "the resync must read the pending store for a merge base — the old code never did");
        // The cache is NOT overwritten with the remote snapshot...
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
        // ...and the concurrently staged row is NOT cleared out from under the user.
        await _pendingChangeStore.DidNotReceive().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        result.ItemsFlushed.ShouldBe(0);
    }

    /// <summary>
    /// The resync must still do its job when there is nothing left staged: with no merge base the
    /// resolver finds nothing to ask about, the rows clear, and the fresh remote lands in the
    /// cache. Without this control, "never write anything" would satisfy the guard above.
    /// </summary>
    [Fact]
    public async Task Resync_WithNothingLeftStaged_StillClearsAndWritesTheFreshRemote()
    {
        var local = CreateWorkItem(1, "Title");
        local.MarkSynced(3);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(local);

        var remote = CreateWorkItem(1, "Title");
        remote.MarkSynced(4);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        var reads = 0;
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads++;
                return reads == 1
                    // Notes only, so the field-change path is skipped and control reaches the resync.
                    ? Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                        new[] { new PendingChangeRecord(1, "note", null, null, "a note") })
                    // Cleared by the note push: no merge base, nothing to ask about.
                    : Task.FromResult<IReadOnlyList<PendingChangeRecord>>(Array.Empty<PendingChangeRecord>());
            });

        var result = await CreateFlusher().FlushAsync([1]);

        reads.ShouldBeGreaterThan(1, "the resync must consult the pending store for a merge base");
        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
        result.ItemsFlushed.ShouldBe(1);
    }

    /// <summary>
    /// The user can still choose to discard: answering "remote" clears the staged rows and takes
    /// the remote snapshot. The point of the slice is that this is now a decision, not a default.
    /// Same concurrent-stage fixture as above — see its remarks for why a plainly staged field
    /// edit would never reach the resync.
    /// </summary>
    [Fact]
    public async Task Resync_UserAcceptsRemote_ClearsStagedRowsAndTakesRemote()
    {
        var local = CreateWorkItem(1, "Local Title");
        local.MarkSynced(3);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(local);

        var remote = CreateWorkItem(1, "Remote Title");
        remote.MarkSynced(9);
        _adoService.FetchAsync(1, Arg.Any<CancellationToken>()).Returns(remote);

        var concurrent = new PendingChangeRecord(1, "field", "System.Title", "Base Title", "Local Title");

        var reads = 0;
        _pendingChangeStore.GetChangesAsync(1, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads++;
                return reads == 1
                    ? Task.FromResult<IReadOnlyList<PendingChangeRecord>>(
                        new[] { new PendingChangeRecord(1, "note", null, null, "a note") })
                    : Task.FromResult<IReadOnlyList<PendingChangeRecord>>(new[] { concurrent });
            });

        AssertConflictPathReachable(local, remote, concurrent, remote.Title);

        _consoleInput.ReadLine().Returns("r");   // accept remote

        await CreateFlusher().FlushAsync([1]);

        await _pendingChangeStore.Received().ClearChangesAsync(1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }
}
