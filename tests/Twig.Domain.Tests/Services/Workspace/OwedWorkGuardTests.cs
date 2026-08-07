using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Workspace;

/// <summary>
/// ADO #147 — seeds and unpushed edits stay visible whatever the Bench selects
/// (docs/specs/bench.spec.md, "The one guard that outranks every selector").
/// <para>
/// 🔴 This is an INVARIANT ON EVALUATION, not a selector. The tests below therefore drive
/// <see cref="BenchEvaluator"/> directly — the same code path a Bench switch will use — with
/// Benches whose selectors match NOTHING relevant. A guard implemented as a selector installed
/// at Bench creation would fail every test here, because none of these Benches was created
/// through that path and one of them has its selectors stripped outright.
/// </para>
/// <para>
/// 🔴 Every test asserts the DISCRIMINATING PRECONDITION explicitly — that the item really is
/// matched by nothing on the Bench under test, via <see cref="BenchMembership.SelectedIds"/>.
/// A fixture that silently started matching the item would otherwise turn these into
/// tautologies, which is a defect this repo has already paid for.
/// </para>
/// </summary>
public sealed class OwedWorkGuardTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();

    /// <summary>The seed from the shared baseline fixture: #-1, which ADO has never heard of.</summary>
    private const int SeedId = -1;

    /// <summary>
    /// The staged edit from the shared baseline fixture: #701, deliberately in a PAST iteration
    /// so the sprint rule cannot reach it. Reused rather than reinvented, so this test is
    /// exercising the same shape the parity baseline already pins down.
    /// </summary>
    private const int StagedEditId = 701;

    public OwedWorkGuardTests()
    {
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());

        // The sprint rule can only ever see items in the CURRENT iteration. #701 is in the past
        // one and the seed is in none, so neither is reachable by any query selector that exists.
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.IterationItems);

        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.Seeds);
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.DirtyItems);
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { WorkingSetBaselineFixture.CurrentIteration });
    }

    private BenchEvaluator CreateSut() => new(_workItemRepo, _calendar, _pendingStore);

    /// <summary>
    /// A Bench that is NOT the default and shares none of its rules: one pin, on an item that is
    /// neither the seed nor the staged edit. This stands in for "the person switched Bench".
    /// </summary>
    private static Bench AnotherBench() => new()
    {
        Name = "another",
        Selectors = [BenchSelector.ForItem(9_000)],
    };

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 1 — a staged edit no selector matches survives a switch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task StagedEdit_MatchedByNoSelectorOnTheBench_IsStillSurfaced()
    {
        var membership = await CreateSut().EvaluateAsync(AnotherBench());

        // 🔴 The precondition. If a later fixture change made #701 match a selector, this test
        // would still pass its assertion below while proving nothing — so assert it here.
        membership.SelectedIds.ShouldNotContain(StagedEditId);
        membership.SelectedIds.ShouldBe(new HashSet<int> { 9_000 }, ignoreOrder: true);

        membership.DirtyItemIds.ShouldContain(StagedEditId);
        membership.AllIds.ShouldContain(StagedEditId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 2 — same for a seed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Seed_MatchedByNoSelectorOnTheBench_IsStillSurfaced()
    {
        var membership = await CreateSut().EvaluateAsync(AnotherBench());

        membership.SelectedIds.ShouldNotContain(SeedId);

        membership.SeedIds.ShouldContain(SeedId);
        membership.AllIds.ShouldContain(SeedId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 4 — the guard is not removable by editing selectors
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ABenchWithNoSelectorsAtAll_StillSurfacesOwedWork()
    {
        // The strongest form of "edit the selectors until it goes away": remove every one.
        var stripped = new Bench { Name = "stripped", Selectors = [] };

        var membership = await CreateSut().EvaluateAsync(stripped);

        // Precondition: the selectors really are gone, so nothing below can be selector-derived.
        membership.SelectedIds.ShouldBeEmpty();

        membership.AllIds.ShouldContain(SeedId);
        membership.AllIds.ShouldContain(StagedEditId);
    }

    [Fact]
    public async Task EveryBench_SurfacesTheSameOwedWork_WhateverItsSelectors()
    {
        var sut = CreateSut();

        var sprintBench = new Bench
        {
            Name = "sprint",
            Selectors = [BenchSelector.ForCurrentSprint(WorkingSetBaselineFixture.UserDisplayName)],
        };
        var pinBench = new Bench { Name = "pins", Selectors = [BenchSelector.ForItem(9_000)] };
        var emptyBench = new Bench { Name = "empty", Selectors = [] };

        var a = await sut.EvaluateAsync(sprintBench);
        var b = await sut.EvaluateAsync(pinBench);
        var c = await sut.EvaluateAsync(emptyBench);

        // Precondition that keeps this from being three copies of one case: the three Benches
        // really do SELECT different things.
        a.SelectedIds.ShouldNotBe(b.SelectedIds);
        c.SelectedIds.ShouldBeEmpty();

        // ...and yet the owed work is identical across all three.
        var owed = new HashSet<int> { SeedId, StagedEditId };
        a.SeedIds.Concat(a.DirtyItemIds).ToHashSet().ShouldBe(owed, ignoreOrder: true);
        b.SeedIds.Concat(b.DirtyItemIds).ToHashSet().ShouldBe(owed, ignoreOrder: true);
        c.SeedIds.Concat(c.DirtyItemIds).ToHashSet().ShouldBe(owed, ignoreOrder: true);
    }

    /// <summary>
    /// The pending store is the OTHER half of what twig owes ADO: an item with staged changes
    /// that the repository does not report as dirty. Covered separately because an implementation
    /// reading only <c>GetDirtyItemsAsync</c> passes every test above.
    /// </summary>
    [Fact]
    public async Task PendingChanges_OnAnItemNoSelectorMatches_AreStillSurfaced()
    {
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(WorkingSetBaselineFixture.PendingIds);

        var membership = await CreateSut().EvaluateAsync(AnotherBench());

        var pendingId = WorkingSetBaselineFixture.PendingIds.Single();

        // Precondition: #702 is not in the cache at all, so no selector can reach it, and it is
        // not among the repository's dirty items either — it exists ONLY in the pending store.
        membership.SelectedIds.ShouldNotContain(pendingId);
        WorkingSetBaselineFixture.DirtyItems.Select(w => w.Id).ShouldNotContain(pendingId);

        membership.DirtyItemIds.ShouldContain(pendingId);
        membership.AllIds.ShouldContain(pendingId);
    }
}
