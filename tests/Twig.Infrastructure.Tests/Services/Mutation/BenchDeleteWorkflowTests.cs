using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Mutation;

/// <summary>
/// ADO #150 — deleting a Bench reports what it holds instead of silently discarding
/// (docs/specs/bench.spec.md §5).
/// <para>
/// Driven at the MUTATION-WORKFLOW seam, the one both the CLI and the agent surface route through,
/// so what deleting a Bench MEANS is tested once rather than once per surface.
/// </para>
/// <para>
/// 🔴 The Bench repository and the pending store are REAL (in-memory SQLite), not substitutes. The
/// whole ticket is about what is and is not WRITTEN — "the Bench survived", "the staged edits
/// survived". A substitute answers from whatever the fixture was told to return, so "nothing was
/// discarded" would pass against an implementation that discarded everything.
/// </para>
/// </summary>
public sealed class BenchDeleteWorkflowTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly IBenchRepository _benchRepo;
    private readonly IPendingChangeStore _pendingStore;

    public BenchDeleteWorkflowTests()
    {
        _benchRepo = new SqliteBenchRepository(_store);
        _pendingStore = new SqlitePendingChangeStore(_store);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
    }

    public void Dispose() => _store.Dispose();

    private DefaultBenchSelectors Selectors => new(userDisplayName: null);
    private CurrentBenchResolver Resolver => new(_benchRepo, Selectors);
    private BenchWorkflow CreateSut() => new(_benchRepo, Selectors, Resolver);
    private PinWorkflow CreatePin() => new(_benchRepo, Selectors, Resolver);

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 1 — a Bench that holds selectors REPORTS them
    //                 and is not discarded
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The red line. A pin is work the person did by hand; ADO has never heard of it, nothing
    /// prompts when it vanishes, and the loss surfaces weeks later. So the delete stops and says
    /// WHICH items are at stake — a count alone cannot answer "do I mind losing these?".
    /// </summary>
    [Fact]
    public async Task Deleting_ABenchThatHoldsPins_ReportsWhatItHolds_AndDeletesNothing()
    {
        var sut = CreateSut();
        var pin = CreatePin();

        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(111, includeSubtree: false);
        await pin.PinAsync(222, includeSubtree: true);

        // Discriminating precondition: the Bench really does hold something, so "it reported what
        // it holds" cannot be satisfied by a Bench that was empty all along.
        var before = (await _benchRepo.GetByNameAsync("release blockers"))!;
        before.Selectors.ShouldNotBeEmpty();

        var outcome = await sut.DeleteAsync("release blockers");

        var holds = outcome.ShouldBeOfType<BenchOutcome.HoldsWork>();
        holds.Bench.Name.ShouldBe("release blockers");
        holds.ItemSelectorIds.ShouldBe(new[] { 111 });
        holds.SubtreeSelectorIds.ShouldBe(new[] { 222 });

        // And the Bench is still there, holding exactly what it held.
        var after = await _benchRepo.GetByNameAsync("release blockers");
        after.ShouldNotBeNull();
        after!.Selectors.ShouldBe(before.Selectors);
    }

    /// <summary>
    /// The report covers query rules too, not only pins. A Bench built around a body of work is
    /// also an arrangement the person made and cannot get back from ADO.
    /// </summary>
    [Fact]
    public async Task Deleting_ABenchThatHoldsAQueryRule_ReportsTheRule()
    {
        var sut = CreateSut();
        await sut.CreateAsync("sprint work");
        var bench = (await _benchRepo.GetByNameAsync("sprint work"))!;
        await _benchRepo.AddSelectorAsync(bench.Id, BenchSelector.ForCurrentSprint("Daniel Green"));

        var holds = (await sut.DeleteAsync("sprint work")).ShouldBeOfType<BenchOutcome.HoldsWork>();

        holds.QueryRules.ShouldBe(new[] { BenchSelector.CurrentSprintRule });
        holds.ItemSelectorIds.ShouldBeEmpty();
        (await _benchRepo.GetByNameAsync("sprint work")).ShouldNotBeNull();
    }

    /// <summary>
    /// Re-typing the name is the way past the report — and it deletes for real, so the report is a
    /// gate rather than a wall.
    /// </summary>
    [Fact]
    public async Task Deleting_WithTheNameRetyped_ActuallyDeletesTheBench()
    {
        var sut = CreateSut();
        var pin = CreatePin();
        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(111, includeSubtree: false);

        var outcome = await sut.DeleteAsync("release blockers", confirmedName: "release blockers");

        outcome.ShouldBeOfType<BenchOutcome.Deleted>().Bench.Name.ShouldBe("release blockers");
        (await _benchRepo.GetByNameAsync("release blockers")).ShouldBeNull();
        (await _benchRepo.GetAllAsync()).ShouldNotContain(b => b.Name == "release blockers");
    }

    /// <summary>
    /// A confirmation that names a DIFFERENT Bench is not a confirmation. Otherwise a script could
    /// carry one hard-coded confirmation string and delete whatever it was pointed at.
    /// </summary>
    [Fact]
    public async Task Deleting_WithAConfirmationNamingAnotherBench_StillReportsAndDeletesNothing()
    {
        var sut = CreateSut();
        var pin = CreatePin();
        await sut.CreateAsync("release blockers");
        await sut.CreateAsync("bugs I own");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(111, includeSubtree: false);

        var outcome = await sut.DeleteAsync("release blockers", confirmedName: "bugs I own");

        outcome.ShouldBeOfType<BenchOutcome.HoldsWork>();
        (await _benchRepo.GetByNameAsync("release blockers")).ShouldNotBeNull();
    }

    /// <summary>
    /// 🔴 An EMPTY Bench goes on the first call. The rule is about not discarding work, and a Bench
    /// holding nothing has none — demanding confirmation for it is exactly the routine ceremony
    /// that trains the reflex the no-force-flag rule exists to prevent.
    /// </summary>
    [Fact]
    public async Task Deleting_AnEmptyBench_NeedsNoConfirmation()
    {
        var sut = CreateSut();
        await sut.CreateAsync("scratch");

        // Discriminating precondition: a NEW Bench really is empty (spec §5), so this is not the
        // confirmed path wearing a disguise.
        (await _benchRepo.GetByNameAsync("scratch"))!.Selectors.ShouldBeEmpty();

        (await sut.DeleteAsync("scratch")).ShouldBeOfType<BenchOutcome.Deleted>();
        (await _benchRepo.GetByNameAsync("scratch")).ShouldBeNull();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 2 — staged edits are UNTOUCHED
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 The property a careless cascade would break. Deleting a Bench is a VIEW operation: the
    /// pending set is work twig owes ADO, and no display preference may destroy it. Asserted
    /// against the real pending store because a substitute cannot be cascaded into.
    /// </summary>
    [Fact]
    public async Task Deleting_ABench_LeavesStagedEditsUntouched()
    {
        var sut = CreateSut();
        var pin = CreatePin();
        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(111, includeSubtree: false);

        await _pendingStore.AddChangeAsync(555, "field", "System.Title", "old", "new");
        await _pendingStore.AddChangeAsync(666, "note", null, null, "a note nobody has pushed");

        // Discriminating precondition: there really is staged work to lose.
        (await _pendingStore.GetDirtyItemIdsAsync()).OrderBy(i => i).ShouldBe(new[] { 555, 666 });

        (await sut.DeleteAsync("release blockers", confirmedName: "release blockers"))
            .ShouldBeOfType<BenchOutcome.Deleted>();

        (await _pendingStore.GetDirtyItemIdsAsync()).OrderBy(i => i).ShouldBe(new[] { 555, 666 });
        (await _pendingStore.GetChangesAsync(555)).Count.ShouldBe(1);
        (await _pendingStore.GetChangesAsync(666)).Count.ShouldBe(1);
    }

    /// <summary>
    /// The cascade must not reach the Bench NEXT DOOR either. Selector rows are keyed by bench id,
    /// and a delete that dropped rows by the wrong predicate would empty an arrangement the person
    /// still has, silently.
    /// </summary>
    [Fact]
    public async Task Deleting_ABench_LeavesOtherBenchesSelectorsIntact()
    {
        var sut = CreateSut();
        var pin = CreatePin();

        await sut.CreateAsync("keep me");
        await sut.SwitchAsync("keep me");
        await pin.PinAsync(777, includeSubtree: false);
        var keptBefore = (await _benchRepo.GetByNameAsync("keep me"))!.Selectors.ToList();
        keptBefore.ShouldNotBeEmpty();

        await sut.CreateAsync("throw me");
        await sut.SwitchAsync("throw me");
        await pin.PinAsync(888, includeSubtree: false);

        await sut.DeleteAsync("throw me", confirmedName: "throw me");

        (await _benchRepo.GetByNameAsync("keep me"))!.Selectors.ShouldBe(keptBefore);
    }

    /// <summary>
    /// Deleting the Bench you are STANDING on leaves you on the default rather than on a Bench that
    /// no longer exists. That is the resolver's documented fallback for a dangling pointer, and it
    /// is asserted here because deletion is the only thing that can produce one.
    /// </summary>
    [Fact]
    public async Task Deleting_TheCurrentBench_LeavesThePersonOnTheDefault()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        (await sut.ListAsync()).CurrentBenchName.ShouldBe("release blockers");

        await sut.DeleteAsync("release blockers", confirmedName: "release blockers");

        (await sut.ListAsync()).CurrentBenchName.ShouldBe(Bench.DefaultName);
    }

    /// <summary>
    /// The selector rows really are gone, not merely unreachable.
    /// </summary>
    /// <remarks>
    /// 🔴 This asserts on STORAGE, which the spec's testing decisions otherwise forbid, and the
    /// reason is specific: there is no behaviour that can see the difference. A first draft tried
    /// to — delete a Bench, create another, assert the new one is empty — and it passed against an
    /// implementation that deleted NO selectors at all, because SQLite's AUTOINCREMENT never
    /// reissues an id, so the orphans could not reattach. That test was a tautology; this one
    /// fails against the same mutation. Left as a row assertion deliberately rather than deleted,
    /// because a store that accumulates unreachable rows for every deleted Bench is a real defect
    /// in a store that is never dropped.
    /// </remarks>
    [Fact]
    public async Task Deleting_ABench_LeavesNoOrphanedSelectorRowsBehind()
    {
        var sut = CreateSut();
        var pin = CreatePin();
        await sut.CreateAsync("throw me");
        await sut.SwitchAsync("throw me");
        await pin.PinAsync(4242, includeSubtree: false);

        var benchId = (await _benchRepo.GetByNameAsync("throw me"))!.Id;
        CountSelectorRowsFor(benchId).ShouldBe(1, "the Bench really does hold a selector to lose");

        await sut.DeleteAsync("throw me", confirmedName: "throw me");

        CountSelectorRowsFor(benchId).ShouldBe(0);
    }

    private int CountSelectorRowsFor(long benchId)
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM bench_selectors WHERE bench_id = @benchId;";
        cmd.Parameters.AddWithValue("@benchId", benchId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 3 — being wrong is loud, and the default stays
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Deleting_ANameThatDoesNotExist_ReportsItAndCreatesNothing()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        var before = (await _benchRepo.GetAllAsync()).Select(b => b.Name).OrderBy(n => n).ToList();

        var unknown = (await sut.DeleteAsync("relase blockers"))
            .ShouldBeOfType<BenchOutcome.UnknownBench>();

        unknown.RequestedName.ShouldBe("relase blockers");
        unknown.KnownBenchNames.ShouldContain("release blockers");
        (await _benchRepo.GetAllAsync()).Select(b => b.Name).OrderBy(n => n).ToList().ShouldBe(before);
    }

    /// <summary>
    /// A typo must not be the command that brings a Bench into being — not even the default, and
    /// not even on the delete path. An implementation that ensured the default before looking the
    /// name up would pass every other test here and fail this one.
    /// </summary>
    [Fact]
    public async Task Deleting_AnUnknownName_OnAnEmptyStore_DoesNotEvenCreateTheDefault()
    {
        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();

        (await CreateSut().DeleteAsync("release blockers")).ShouldBeOfType<BenchOutcome.UnknownBench>();

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_TheDefaultBench_IsRefused()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");   // ensures the default exists

        (await sut.DeleteAsync(Bench.DefaultName))
            .ShouldBeOfType<BenchOutcome.DefaultBenchCannotBeDeleted>();

        (await _benchRepo.GetByNameAsync(Bench.DefaultName)).ShouldNotBeNull();
    }

    /// <summary>
    /// 🔴 And re-typing the name does NOT get you past it. The default cannot go missing (spec §4);
    /// if confirmation could remove it, every rule that leans on "the default always resolves"
    /// would be conditionally false, including the unknown-Bench error's own escape hatch.
    /// </summary>
    [Fact]
    public async Task Deleting_TheDefaultBench_IsRefusedEvenWithTheNameRetyped()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");

        (await sut.DeleteAsync(Bench.DefaultName, confirmedName: Bench.DefaultName))
            .ShouldBeOfType<BenchOutcome.DefaultBenchCannotBeDeleted>();

        (await _benchRepo.GetByNameAsync(Bench.DefaultName)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Deleting_TheDefaultBench_OnAFreshStore_IsRefusedAndCreatesNothing()
    {
        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();

        (await CreateSut().DeleteAsync(Bench.DefaultName))
            .ShouldBeOfType<BenchOutcome.DefaultBenchCannotBeDeleted>();

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_IsCaseInsensitive_LikeEveryOtherBenchLookup()
    {
        var sut = CreateSut();
        await sut.CreateAsync("Release Blockers");

        var outcome = await sut.DeleteAsync("release blockers");

        // The STORED spelling comes back, so the person is told which Bench they were about to
        // remove rather than having their own typing echoed at them.
        outcome.ShouldBeOfType<BenchOutcome.Deleted>().Bench.Name.ShouldBe("Release Blockers");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Deleting_ABlankName_IsRefusedAndDeletesNothing(string name)
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        var before = (await _benchRepo.GetAllAsync()).Count;

        (await sut.DeleteAsync(name)).ShouldBeOfType<BenchOutcome.NameRejected>();

        (await _benchRepo.GetAllAsync()).Count.ShouldBe(before);
    }
}
