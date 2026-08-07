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
/// ADO #149 — put one arrangement down and pick another up; an unknown Bench fails loudly
/// (docs/specs/bench.spec.md §5).
/// <para>
/// Driven at the MUTATION-WORKFLOW seam, the one both the CLI and the agent surface route through.
/// Testing through the adapters instead would test the same logic twice and let the two drift.
/// </para>
/// <para>
/// 🔴 The Bench repository is REAL (in-memory SQLite), not a substitute. The whole ticket is about
/// what does and does not get WRITTEN when a name does not resolve; a substitute would answer from
/// whatever the fixture was told to return, and "created nothing" would pass against an
/// implementation that created something.
/// </para>
/// </summary>
public sealed class BenchSwitchWorkflowTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly IBenchRepository _benchRepo;

    private static readonly IterationPath Sprint = IterationPath.Parse(@"Project\Sprint 7").Value;

    public BenchSwitchWorkflowTests()
    {
        _benchRepo = new SqliteBenchRepository(_store);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetDirtyItemsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _pendingStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Sprint });
    }

    public void Dispose() => _store.Dispose();

    private DefaultBenchSelectors Selectors => new(_trackingRepo, userDisplayName: null);
    private CurrentBenchResolver Resolver => new(_benchRepo, Selectors);
    private BenchWorkflow CreateSut() => new(_benchRepo, Selectors, Resolver);
    private PinWorkflow CreatePin() => new(_benchRepo, Selectors, trackingRepository: null, Resolver);

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 1 — switching changes which items the view shows,
    //  and the previous Bench is unchanged when you switch back
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The observable claim: what a person sees changes when they switch, and changes BACK when
    /// they switch back. Membership is read through the evaluator — the same path the view uses —
    /// rather than by inspecting rows, because storage shape is deliberately not the contract.
    /// </summary>
    [Fact]
    public async Task Switching_ChangesWhichItemsAreOnTheBench_AndSwitchingBackRestoresTheFirst()
    {
        var sut = CreateSut();
        var pin = CreatePin();

        // On the default Bench, pin 111.
        await pin.PinAsync(111, includeSubtree: false);

        (await sut.CreateAsync("release blockers")).ShouldBeOfType<BenchOutcome.Created>();
        (await sut.SwitchAsync("release blockers")).ShouldBeOfType<BenchOutcome.Switched>();

        // Discriminating precondition: the NEW Bench really does not already show 111, so
        // "switching changed the view" cannot be satisfied by two Benches that look the same.
        var onNew = await MembershipAsync();
        onNew.ShouldNotContain(111);

        await pin.PinAsync(222, includeSubtree: false);
        (await MembershipAsync()).ShouldBe(new[] { 222 });

        (await sut.SwitchAsync(Bench.DefaultName)).ShouldBeOfType<BenchOutcome.Switched>();

        // The Bench left behind was not dismantled: it holds exactly what it held, and nothing
        // leaked across from the one that was picked up in between.
        var back = await MembershipAsync();
        back.ShouldContain(111);
        back.ShouldNotContain(222);
    }

    [Fact]
    public async Task Switching_LeavesTheBenchYouLeftUntouched()
    {
        var sut = CreateSut();
        var pin = CreatePin();
        await pin.PinAsync(111, includeSubtree: false);

        var before = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ToList();
        before.ShouldNotBeEmpty();

        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(999, includeSubtree: true);

        var after = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ToList();
        after.ShouldBe(before);
    }

    [Fact]
    public async Task Switching_MovesWhichBenchAPinLandsOn()
    {
        var sut = CreateSut();
        var pin = CreatePin();

        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");
        await pin.PinAsync(4242, includeSubtree: false);

        var target = (await _benchRepo.GetByNameAsync("release blockers"))!;
        target.Selectors.ShouldContain(s => s.Kind == SelectorKind.Item && s.Payload == "4242");

        // And NOT on the default: a pin landing on the Bench the person is not looking at is
        // exactly the silent-wrong-target failure this ticket exists to prevent.
        var def = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!;
        def.Selectors.ShouldNotContain(s => s.Kind == SelectorKind.Item && s.Payload == "4242");
    }

    [Fact]
    public async Task List_ReportsTheBenchSwitchedTo_AsTheCurrentOne()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");

        (await sut.ListAsync()).CurrentBenchName.ShouldBe(Bench.DefaultName);

        await sut.SwitchAsync("release blockers");

        var listing = await sut.ListAsync();
        listing.CurrentBenchName.ShouldBe("release blockers");
        listing.Benches.ShouldContain(b => b.Name == listing.CurrentBenchName);
    }

    [Fact]
    public async Task TheCurrentBench_SurvivesANewProcess_ReadingTheSameStore()
    {
        await CreateSut().CreateAsync("release blockers");
        await CreateSut().SwitchAsync("release blockers");

        // A fresh workflow over a fresh repository instance on the same store: an implementation
        // that only remembered the switch in memory fails here.
        var freshRepo = new SqliteBenchRepository(_store);
        var freshResolver = new CurrentBenchResolver(freshRepo, Selectors);
        (await freshResolver.ResolveAsync()).Name.ShouldBe("release blockers");
    }

    [Fact]
    public async Task Switching_IsCaseInsensitive_LikeEveryOtherBenchLookup()
    {
        var sut = CreateSut();
        await sut.CreateAsync("Release Blockers");

        var outcome = await sut.SwitchAsync("release blockers");

        // The STORED spelling is what comes back, so the person is told which Bench they landed on
        // rather than having their own typing echoed at them.
        outcome.ShouldBeOfType<BenchOutcome.Switched>().Bench.Name.ShouldBe("Release Blockers");
    }

    [Fact]
    public async Task SwitchingToTheDefault_WorksOnAFreshStore_WhereNothingHasCreatedItYet()
    {
        // The default cannot go missing (spec §4), so it is the ONE name that must resolve before
        // anything has created it — and it is never subject to the unknown-Bench error.
        (await _benchRepo.GetByNameAsync(Bench.DefaultName)).ShouldBeNull();

        var outcome = await CreateSut().SwitchAsync(Bench.DefaultName);

        outcome.ShouldBeOfType<BenchOutcome.Switched>().Bench.IsDefault.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 2 — an unknown Bench is a HARD ERROR that creates nothing
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Switching_ToANameThatDoesNotExist_ReportsItAndNamesWhatWasAskedFor()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");

        var outcome = await sut.SwitchAsync("relase blockers");

        var unknown = outcome.ShouldBeOfType<BenchOutcome.UnknownBench>();
        unknown.RequestedName.ShouldBe("relase blockers");
        // It says what DOES exist, so the person can act rather than only learn they were wrong.
        unknown.KnownBenchNames.ShouldContain("release blockers");
    }

    /// <summary>
    /// 🔴 The half that silently regresses, asserted on its own: a miss must CREATE NOTHING.
    /// A Bench created on reference reproduces the exact defect being escaped one level up — the
    /// person believes they are standing on an arrangement they built and is in fact on an empty
    /// one, with nothing saying so.
    /// </summary>
    [Fact]
    public async Task Switching_ToANameThatDoesNotExist_CreatesNothing()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");

        var namesBefore = (await _benchRepo.GetAllAsync()).Select(b => b.Name).OrderBy(n => n).ToList();
        namesBefore.ShouldNotContain("relase blockers");

        await sut.SwitchAsync("relase blockers");

        var namesAfter = (await _benchRepo.GetAllAsync()).Select(b => b.Name).OrderBy(n => n).ToList();
        namesAfter.ShouldBe(namesBefore);
    }

    /// <summary>
    /// The stricter form of the same rule: on a store where NOTHING exists yet, a typo must not
    /// even be the command that brings the default Bench into being. An implementation that
    /// ensured the default before looking the name up would pass the test above and fail this one.
    /// </summary>
    [Fact]
    public async Task Switching_ToAnUnknownName_OnAnEmptyStore_DoesNotEvenCreateTheDefault()
    {
        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();

        var outcome = await CreateSut().SwitchAsync("release blockers");

        outcome.ShouldBeOfType<BenchOutcome.UnknownBench>();
        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Switching_ToANameThatDoesNotExist_LeavesTheCurrentBenchWhereItWas()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        await sut.SwitchAsync("release blockers");

        await sut.SwitchAsync("nonexistent");

        // A failed switch is not a switch to nowhere: the person stays exactly where they were.
        (await sut.ListAsync()).CurrentBenchName.ShouldBe("release blockers");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Switching_ToABlankName_IsRefusedAndCreatesNothing(string name)
    {
        var sut = CreateSut();
        var before = (await _benchRepo.GetAllAsync()).Count;

        (await sut.SwitchAsync(name)).ShouldBeOfType<BenchOutcome.NameRejected>();

        (await _benchRepo.GetAllAsync()).Count.ShouldBe(before);
    }

    /// <summary>
    /// Membership through the evaluator — the same path the view uses — so these tests assert what
    /// a person would SEE rather than which rows exist.
    /// </summary>
    private async Task<IReadOnlyList<int>> MembershipAsync()
    {
        var bench = await Resolver.ResolveAsync();
        var membership = await new BenchEvaluator(_workItemRepo, _calendar, _pendingStore)
            .EvaluateAsync(bench);
        return membership.AllIds.OrderBy(i => i).ToList();
    }
}
