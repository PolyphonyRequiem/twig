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
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Mutation;

/// <summary>
/// ADO #145 — pinning and unpinning act on the CURRENT BENCH, not on the file
/// (docs/specs/bench.spec.md, Testing Decisions).
/// <para>
/// Driven at the MUTATION-WORKFLOW seam, which both the CLI and the agent surface route through.
/// Testing through the two adapters instead would test the same logic twice and let them drift —
/// the defect that made every agent-surface tool name its own target.
/// </para>
/// <para>
/// 🔴 The Bench repository here is REAL (in-memory SQLite), not a substitute. Selectors are
/// written and read back within one test, and a substitute returning an empty Bench would make
/// every assertion about membership pass vacuously while looking plausible.
/// </para>
/// </summary>
public sealed class PinWorkflowTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly IBenchRepository _benchRepo;

    private static readonly IterationPath Sprint = IterationPath.Parse(@"Project\Sprint 7").Value;

    public PinWorkflowTests()
    {
        _benchRepo = new SqliteBenchRepository(_store);

        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Sprint });
    }

    public void Dispose() => _store.Dispose();

    private PinWorkflow CreateSut() => new(
        _benchRepo,
        new DefaultBenchSelectors(_trackingRepo, userDisplayName: null),
        _trackingRepo);

    /// <summary>What the person actually sees: the ids the current Bench evaluates to.</summary>
    private async Task<IReadOnlySet<int>> ViewAsync()
    {
        var bench = await _benchRepo.GetOrCreateDefaultAsync(
            await new DefaultBenchSelectors(_trackingRepo, null).BuildAsync());
        var membership = await new BenchEvaluator(_workItemRepo, _calendar, _pendingStore).EvaluateAsync(bench);
        return membership.AllIds;
    }

    private async Task<IReadOnlyCollection<BenchSelector>> SelectorsAsync()
        => (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors;

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 1 — pinning adds an ITEM SELECTOR and the item appears
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pin_AddsAnItemSelectorToTheBench_AndTheItemAppearsInTheView()
    {
        // Precondition that stops this degrading into a tautology: nothing selects 42 to begin
        // with, so its appearance below can only come from the pin.
        (await ViewAsync()).ShouldNotContain(42);

        await CreateSut().PinAsync(42, includeSubtree: false);

        (await SelectorsAsync()).ShouldContain(BenchSelector.ForItem(42));
        (await ViewAsync()).ShouldContain(42);
    }

    [Fact]
    public async Task PinTree_AddsASubtreeSelector_NotAnItemSelector()
    {
        await CreateSut().PinAsync(100, includeSubtree: true);

        var selectors = await SelectorsAsync();
        selectors.ShouldContain(BenchSelector.ForSubtree(100));
        selectors.ShouldNotContain(BenchSelector.ForItem(100));
    }

    [Fact]
    public async Task Pin_IsIdempotent_SoRepetitionCannotChangeMembership()
    {
        var sut = CreateSut();
        await sut.PinAsync(42, includeSubtree: false);
        await sut.PinAsync(42, includeSubtree: false);

        (await SelectorsAsync()).Count(s => s == BenchSelector.ForItem(42)).ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 2 — unpinning removes it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unpin_RemovesTheSelector_AndTheItemLeavesTheView()
    {
        var sut = CreateSut();
        await sut.PinAsync(42, includeSubtree: false);

        // The discriminating precondition: it really was there before the unpin.
        (await ViewAsync()).ShouldContain(42);

        var outcome = await sut.UnpinAsync(42);

        outcome.ShouldBeOfType<PinOutcome.Unpinned>().WasPinned.ShouldBeTrue();
        (await SelectorsAsync()).ShouldNotContain(BenchSelector.ForItem(42));
        (await ViewAsync()).ShouldNotContain(42);
    }

    [Fact]
    public async Task Unpin_RemovesASubtreePinToo_BecauseThePersonAskedToStopFollowingTheItem()
    {
        var sut = CreateSut();
        await sut.PinAsync(100, includeSubtree: true);

        await sut.UnpinAsync(100);

        (await SelectorsAsync()).ShouldNotContain(BenchSelector.ForSubtree(100));
    }

    [Fact]
    public async Task Unpin_WhenNothingWasPinned_ReportsItRatherThanFailing()
    {
        var outcome = await CreateSut().UnpinAsync(999);

        outcome.ShouldBeOfType<PinOutcome.Unpinned>().WasPinned.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 3 — a SUBTREE selector matches a child created AFTER it
    //  🔴 This is the one a naive implementation gets wrong while passing
    //     everything else: expanding the subtree at pin time and storing ids.
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SubtreePin_MatchesAChildCreatedAfterTheSelectorWasAdded()
    {
        await CreateSut().PinAsync(100, includeSubtree: true);

        // The precondition: at pin time the subtree was EMPTY. If the fixture ever gains a child
        // here, the test stops discriminating between live expansion and a pin-time snapshot.
        (await _workItemRepo.GetChildrenAsync(100)).ShouldBeEmpty();
        (await ViewAsync()).ShouldNotContain(101);

        // ...and only now does the child exist.
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(101, "Created later").WithParent(100).Build() });

        (await ViewAsync()).ShouldContain(101);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 4 — pinning an item a QUERY selector already matches
    //  yields ONE copy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task PinningAnItemTheSprintQueryAlreadyMatches_YieldsOneCopy()
    {
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(500, "In the sprint").WithIterationPath(Sprint.Value).Build(),
            });

        // The precondition that makes the overlap real: the query matches 500 BEFORE the pin.
        // Without this a fixture whose query matched nothing would pass the count assertion below
        // while proving nothing about overlap at all.
        (await ViewAsync()).ShouldContain(500);

        await CreateSut().PinAsync(500, includeSubtree: false);

        var bench = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!;
        var membership = await new BenchEvaluator(_workItemRepo, _calendar, _pendingStore).EvaluateAsync(bench);

        // Matched by BOTH selectors — the presentation split keeps it in both categories, and
        // membership is a union, so it is present exactly once.
        membership.QueryMatches.Select(w => w.Id).ShouldContain(500);
        membership.PinnedIds.ShouldContain(500);
        membership.AllIds.Count(id => id == 500).ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Coexistence with the file, until #146
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pin_AlsoWritesTheFile_BecauseItRemainsTheLiveSourceUntilTheMigration()
    {
        // The file still drives tracked-tree refresh, the cleanup policy, and tracking status.
        // Writing only the Bench would stop tracked trees being refreshed — a regression the
        // parity baseline cannot see, because it covers the view and not the sync path.
        await CreateSut().PinAsync(42, includeSubtree: true);

        await _trackingRepo.Received(1).UpsertTrackedAsync(42, TrackingMode.Tree, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unpin_AlsoClearsTheFile()
    {
        await CreateSut().UnpinAsync(42);

        await _trackingRepo.Received(1).RemoveTrackedAsync(42, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  🔴 TEST 12 — A LOCK, NOT A REGRESSION TEST
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 LOCK, NOT A REGRESSION TEST. This PASSES on the unfixed code, by design: reading a work
    /// item by id has never touched a Bench. It is written because #145 adds WRITE paths near the
    /// read path and could plausibly break it — a targeted read that mutates the Bench would move
    /// the person's view out from under them (spec story 31).
    /// <para>
    /// It must NOT be counted as evidence that this change works. Confusing a lock with a
    /// regression test is how a suite grows while its defensive power does not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Lock_ReadingAWorkItemById_LeavesTheBenchesSelectorsByteIdentical()
    {
        var sut = CreateSut();
        await sut.PinAsync(42, includeSubtree: false);
        await sut.PinAsync(100, includeSubtree: true);

        var before = Serialise(await SelectorsAsync());

        // The precondition: the Bench is NOT empty, so "unchanged" is a real claim about content
        // rather than two empty strings comparing equal.
        before.ShouldNotBeNullOrWhiteSpace();
        (await SelectorsAsync()).Count.ShouldBeGreaterThan(1);

        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemBuilder(42, "Read by id").Build());
        (await _workItemRepo.GetByIdAsync(42)).ShouldNotBeNull();

        Serialise(await SelectorsAsync()).ShouldBe(before);
    }

    private static string Serialise(IReadOnlyCollection<BenchSelector> selectors)
        => string.Join("\n", selectors
            .Select(s => $"{s.Kind}\u001f{s.Payload}")
            .OrderBy(s => s, StringComparer.Ordinal));
}
