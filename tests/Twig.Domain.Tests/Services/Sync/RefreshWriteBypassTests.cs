using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Sync;

/// <summary>
/// Wayfinder 0004 slice 5 — the write bypasses in <see cref="RefreshOrchestrator"/> are absorbed.
/// </summary>
/// <remarks>
/// <para>
/// Two distinct defects lived here, and they are asserted separately because only one of them
/// was reachable by choice:
/// </para>
/// <list type="number">
///   <item>
///     <c>FetchItemsAsync</c> took a <c>force</c> flag that emptied the protected set AND swapped
///     the save path to raw <see cref="IWorkItemRepository.SaveBatchAsync"/>. Emptying the set
///     also short-circuited conflict detection, so <c>--force</c> suppressed the report of the
///     very overwrite it performed. The parameter is gone; <see cref="ForceParameterIsGone"/>
///     pins that at the signature level so it cannot creep back as an optional argument.
///   </item>
///   <item>
///     <c>HydrateAncestorsAsync</c> wrote ancestors with a raw <c>SaveBatchAsync</c> and was
///     <b>never</b> behind <c>force</c> at all — a live data-loss route on the default path that
///     no user opted into. <see cref="HydrateAncestors_DoesNotOverwriteAProtectedAncestor"/>
///     covers the DEFAULT path specifically.
///   </item>
/// </list>
/// <para>
/// These fixtures assert on <see cref="IWorkItemRepository"/> rather than on
/// <see cref="ProtectedCacheWriter"/> (a concrete class, not substitutable) — the repository is
/// where an unprotected write would actually land, so it is the honest observation point.
/// </para>
/// </remarks>
public class RefreshWriteBypassTests
{
    private readonly IContextStore _contextStore = Substitute.For<IContextStore>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IPendingChangeStore _pendingChangeStore = Substitute.For<IPendingChangeStore>();
    private readonly RefreshOrchestrator _orchestrator;

    public RefreshWriteBypassTests()
    {
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);

        var workingSetService = new WorkingSetService(
            _contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, _adoService, protectedCacheWriter, _pendingChangeStore, null, 30, 30);

        _orchestrator = new RefreshOrchestrator(
            _contextStore, _workItemRepo, _adoService, _pendingChangeStore, protectedCacheWriter,
            workingSetService, syncCoordinatorFactory, _iterationService, null);

        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
    }

    /// <summary>
    /// Marks <paramref name="id"/> protected via the pending store and returns the local mirror
    /// the repository will hand back, so a fixture can assert what would have been clobbered.
    /// </summary>
    private WorkItem GivenProtectedItem(int id, int localRevision)
    {
        var local = new WorkItemBuilder(id, "Local").Build();
        local.MarkSynced(localRevision);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(new[] { id });
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(local);
        return local;
    }

    private static WorkItem RemoteAt(int id, int revision)
    {
        var remote = new WorkItemBuilder(id, "Remote").Build();
        remote.MarkSynced(revision);
        return remote;
    }

    // ── Site 1: FetchItemsAsync ─────────────────────────────────────

    /// <summary>
    /// The signature guard. A behavioural test alone would pass against a <c>force</c> parameter
    /// that merely defaulted to <c>false</c>, leaving the bypass one argument away.
    /// </summary>
    [Fact]
    public void ForceParameterIsGone()
    {
        var fetchItems = typeof(RefreshOrchestrator)
            .GetMethod(nameof(RefreshOrchestrator.FetchItemsAsync));

        fetchItems.ShouldNotBeNull();
        fetchItems!.GetParameters()
            .Any(p => p.Name is not null
                      && p.Name.Contains("force", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse(
                "0004 slice 5 deleted the force bypass outright; a defaulted parameter would " +
                "leave the overwrite path reachable by any caller that passes true");
    }

    /// <summary>
    /// The behavioural half: a protected sprint item is never written on the only path there is.
    /// </summary>
    [Fact]
    public async Task FetchItems_ProtectedSprintItem_IsNeverWrittenToTheRepository()
    {
        var local = GivenProtectedItem(1, localRevision: 3);
        var remote = RemoteAt(1, revision: 7);

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remote });

        // Precondition: the remote must genuinely be ahead, or the conflict branch never runs
        // and this fixture would pass for the wrong reason.
        local.Revision.ShouldBeLessThan(remote.Revision);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 1)),
            Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 1), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The interaction the ticket called out: emptying <c>protectedIds</c> also disabled the
    /// conflict report, because <c>FindConflictsAsync</c> returns <c>[]</c> for an empty set.
    /// With no way to empty it, the overwrite is both prevented AND reported.
    /// </summary>
    [Fact]
    public async Task FetchItems_ProtectedItemWithNewerRemote_StillReportsTheConflict()
    {
        var local = GivenProtectedItem(1, localRevision: 3);
        var remote = RemoteAt(1, revision: 7);

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { remote });

        local.Revision.ShouldBeLessThan(remote.Revision);

        var result = await _orchestrator.FetchItemsAsync("SELECT ...");

        result.Conflicts.Count.ShouldBe(1,
            "the conflict report used to be suppressed as a side effect of --force emptying " +
            "the protected set, so the user was not told what had been overwritten");
        result.Conflicts[0].Id.ShouldBe(1);
        result.Conflicts[0].LocalRevision.ShouldBe(3);
        result.Conflicts[0].RemoteRevision.ShouldBe(7);
    }

    /// <summary>
    /// The active item took its own <c>SaveAsync</c> branch under <c>force</c>, distinct from the
    /// batch path, so it needs its own guard.
    /// </summary>
    [Fact]
    public async Task FetchItems_ProtectedActiveItem_IsNeverWrittenToTheRepository()
    {
        var local = GivenProtectedItem(42, localRevision: 2);
        var remoteActive = RemoteAt(42, revision: 9);

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(1, "Sprint").Build() });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(remoteActive);
        _adoService.FetchChildrenAsync(42, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        local.Revision.ShouldBeLessThan(remoteActive.Revision);

        await _orchestrator.FetchItemsAsync("SELECT ...");

        await _workItemRepo.DidNotReceive().SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == 42), Arg.Any<CancellationToken>());
    }

    // ── Site 2: HydrateAncestorsAsync (the DEFAULT path) ────────────

    /// <summary>
    /// The highest-value guard in this slice. No flag was ever involved: every refresh hydrated
    /// ancestors with an unprotected <c>SaveBatchAsync</c>, so a staged edit on a parent item was
    /// silently destroyed on the default path.
    /// </summary>
    [Fact]
    public async Task HydrateAncestors_DoesNotOverwriteAProtectedAncestor()
    {
        GivenProtectedItem(5, localRevision: 4);
        var remoteAncestor = RemoteAt(5, revision: 11);

        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<int>>(new[] { 5 })
                    : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            });
        _adoService.FetchBatchAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>())
            .Returns(new[] { remoteAncestor });

        // No `force` argument anywhere: this IS the default path.
        await _orchestrator.HydrateAncestorsAsync();

        callCount.ShouldBeGreaterThan(0, "the hydration loop must actually have run");
        await _workItemRepo.DidNotReceive().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 5)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The guard must not cost hydration its job: an unprotected ancestor still gets written.
    /// Without this, "protect everything by never writing" would pass the test above.
    /// </summary>
    [Fact]
    public async Task HydrateAncestors_StillWritesAnUnprotectedAncestor()
    {
        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult<IReadOnlyList<int>>(new[] { 5 })
                    : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
            });
        _adoService.FetchBatchAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(5, "Parent").Build() });

        await _orchestrator.HydrateAncestorsAsync();

        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 5)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Termination must key off what was FETCHED, not what was written. A level whose ancestors
    /// are all protected still resolves orphan parents for the next iteration, so breaking on an
    /// empty write would leave the hierarchy half-hydrated exactly when the user has an ancestor
    /// staged — turning the new guard into a second, quieter defect.
    /// </summary>
    [Fact]
    public async Task HydrateAncestors_ProtectedLevel_DoesNotHaltHydrationOfTheNextLevel()
    {
        GivenProtectedItem(5, localRevision: 4);

        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => Task.FromResult<IReadOnlyList<int>>(new[] { 5 }),   // protected
                    2 => Task.FromResult<IReadOnlyList<int>>(new[] { 6 }),   // grandparent
                    _ => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
                };
            });
        _adoService.FetchBatchAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>())
            .Returns(new[] { RemoteAt(5, revision: 11) });
        _adoService.FetchBatchAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(6)), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(6, "Grandparent").Build() });

        await _orchestrator.HydrateAncestorsAsync();

        await _workItemRepo.Received().SaveBatchAsync(
            Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == 6)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Termination must not silently rely on the 5-level cap. A fully-protected level leaves its
    /// orphan in place by design, so <c>GetOrphanParentIdsAsync</c> keeps returning the same id;
    /// without a seen-set the loop re-fetches it from ADO on every remaining level.
    /// </summary>
    [Fact]
    public async Task HydrateAncestors_FullyProtectedLevel_DoesNotRefetchTheSameAncestorRepeatedly()
    {
        GivenProtectedItem(5, localRevision: 4);

        // The orphan never resolves, because the protected write deliberately skips it.
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>(new[] { 5 }));
        _adoService.FetchBatchAsync(
                Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>())
            .Returns(new[] { RemoteAt(5, revision: 11) });

        await _orchestrator.HydrateAncestorsAsync();

        await _adoService.Received(1).FetchBatchAsync(
            Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(5)), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The seen-set must not cost hydration its job: distinct ancestors at successive levels are
    /// still walked. Without this control, "fetch nothing after level 1" would satisfy the guard
    /// above.
    /// </summary>
    [Fact]
    public async Task HydrateAncestors_DistinctAncestorPerLevel_StillWalksEveryLevel()
    {
        var callCount = 0;
        _workItemRepo.GetOrphanParentIdsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount switch
                {
                    1 => Task.FromResult<IReadOnlyList<int>>(new[] { 5 }),
                    2 => Task.FromResult<IReadOnlyList<int>>(new[] { 6 }),
                    3 => Task.FromResult<IReadOnlyList<int>>(new[] { 7 }),
                    _ => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
                };
            });
        foreach (var id in new[] { 5, 6, 7 })
        {
            var captured = id;
            _adoService.FetchBatchAsync(
                    Arg.Is<IReadOnlyList<int>>(ids => ids.Contains(captured)), Arg.Any<CancellationToken>())
                .Returns(new[] { new WorkItemBuilder(captured, $"Ancestor{captured}").Build() });
        }

        await _orchestrator.HydrateAncestorsAsync();

        foreach (var id in new[] { 5, 6, 7 })
        {
            var captured = id;
            await _workItemRepo.Received().SaveBatchAsync(
                Arg.Is<IReadOnlyList<WorkItem>>(items => items.Any(i => i.Id == captured)),
                Arg.Any<CancellationToken>());
        }
    }
}
