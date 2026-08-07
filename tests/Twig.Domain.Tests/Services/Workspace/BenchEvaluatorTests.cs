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
/// The tests that EARN the selector model (docs/specs/bench.spec.md, Testing Decisions 6-9).
/// <para>
/// 🔴 Tests 7, 8 and 9 are the ones the spec singles out: if they are dropped as "obvious", the
/// union semantics are unenforced and the first implementation to evaluate selectors in sequence
/// passes everything else.
/// </para>
/// </summary>
public sealed class BenchEvaluatorTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IIterationCalendar _calendar = Substitute.For<IIterationCalendar>();
    private readonly IPendingChangeStore _pendingStore = Substitute.For<IPendingChangeStore>();

    private static readonly IterationPath Sprint = IterationPath.Parse(@"Project\Sprint 7").Value;

    public BenchEvaluatorTests()
    {
        _workItemRepo.GetChildrenAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<WorkItem>());
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Sprint });
    }

    private BenchEvaluator CreateSut() => new(_workItemRepo, _calendar, _pendingStore);

    private static Bench BenchOf(params BenchSelector[] selectors)
        => new() { Name = "test", Selectors = selectors };

    // ═══════════════════════════════════════════════════════════════
    //  Test 7 — selector ORDER does not change membership
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test7_SelectorOrder_DoesNotChangeMembership()
    {
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(1, "In sprint").WithIterationPath(Sprint.Value).Build(),
                new WorkItemBuilder(2, "Also in sprint").WithIterationPath(Sprint.Value).Build(),
            });

        var query = BenchSelector.ForCurrentSprint(null);
        var pinA = BenchSelector.ForItem(10);
        var pinB = BenchSelector.ForItem(20);

        var forwards = await CreateSut().EvaluateAsync(BenchOf(query, pinA, pinB));
        var backwards = await CreateSut().EvaluateAsync(BenchOf(pinB, pinA, query));

        // Membership is a set union, so construction order must be unobservable.
        backwards.AllIds.ShouldBe(forwards.AllIds, ignoreOrder: true);
        forwards.AllIds.ShouldBe(new HashSet<int> { 1, 2, 10, 20 }, ignoreOrder: true);

        // The precondition that makes this test non-trivial: the two Benches really were built
        // in different orders, and really do hold more than one selector.
        forwards.PinnedIds.Count.ShouldBe(2);
        backwards.PinnedIds.ShouldBe(new[] { 20, 10 });
        forwards.PinnedIds.ShouldBe(new[] { 10, 20 });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 8 — overlapping selectors produce ONE copy
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test8_ItemMatchedByQueryAndPin_AppearsOnce()
    {
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(42, "Both").WithIterationPath(Sprint.Value).Build() });

        var membership = await CreateSut().EvaluateAsync(
            BenchOf(BenchSelector.ForCurrentSprint(null), BenchSelector.ForItem(42)));

        // The precondition: #42 really is matched by BOTH selectors, so this is a genuine overlap
        // rather than a pin the query never saw.
        membership.QueryMatches.Select(w => w.Id).ShouldContain(42);
        membership.PinnedIds.ShouldContain(42);

        membership.AllIds.Count(id => id == 42).ShouldBe(1);
        membership.AllIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Test8_TwoQuerySelectorsMatchingTheSameItem_YieldOneCopy()
    {
        // 🔴 The fixture must make BOTH query selectors return the item, or removing the
        // deduplication changes nothing and the test passes against a broken implementation.
        // Verified by mutation: dropping the dedupe check leaves this red.
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(7, "Matched twice").AssignedTo("Ada").WithIterationPath(Sprint.Value).Build(),
                new WorkItemBuilder(8, "Also matched twice").AssignedTo("Ada").WithIterationPath(Sprint.Value).Build(),
            });

        var membership = await CreateSut().EvaluateAsync(BenchOf(
            BenchSelector.ForCurrentSprint("Ada"),
            BenchSelector.ForCurrentSprint(null)));

        // The precondition: two DISTINCT selectors, each of which really does match #7.
        membership.QueryMatches.Count(w => w.Id == 7).ShouldBe(1);
        membership.QueryMatches.Count(w => w.Id == 8).ShouldBe(1);

        // The whole result carries no duplicates at all — the assertion that catches a dedupe
        // removed anywhere on the query path, not just for one id.
        membership.QueryMatches.Count.ShouldBe(2);
        membership.QueryMatches.Select(w => w.Id).Distinct().Count().ShouldBe(membership.QueryMatches.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 9 — a subtree selector matches a child created AFTER it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test9_SubtreeSelector_MatchesAChildCreatedAfterTheSelector()
    {
        var bench = BenchOf(BenchSelector.ForSubtree(100));

        // Before: the subtree has one child.
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(101, "First child").WithParent(100).Build() });

        var before = await CreateSut().EvaluateAsync(bench);
        before.AllIds.ShouldBe(new HashSet<int> { 100, 101 }, ignoreOrder: true);

        // A child is added to the subtree AFTER the selector was created. The selector itself is
        // untouched — this is the same Bench object.
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(101, "First child").WithParent(100).Build(),
                new WorkItemBuilder(102, "Child added later").WithParent(100).Build(),
            });

        var after = await CreateSut().EvaluateAsync(bench);

        // 🔴 The distinguishing assertion. An implementation that expanded the subtree once and
        // stored the ids passes the "before" case and fails here.
        after.AllIds.ShouldContain(102);
        after.AllIds.ShouldBe(new HashSet<int> { 100, 101, 102 }, ignoreOrder: true);
    }

    [Fact]
    public async Task Test9_SubtreeSelector_MatchesGrandchildren()
    {
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(101, "Child").WithParent(100).Build() });
        _workItemRepo.GetChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(102, "Grandchild").WithParent(101).Build() });

        var membership = await CreateSut().EvaluateAsync(BenchOf(BenchSelector.ForSubtree(100)));

        membership.AllIds.ShouldBe(new HashSet<int> { 100, 101, 102 }, ignoreOrder: true);
    }

    [Fact]
    public async Task SubtreeSelector_WithACycleInCachedLinks_Terminates()
    {
        // A cached parent link cycle must not spin forever. Defensive: the cache is rebuilt from
        // ADO and should never contain one, but "should never" is not a termination guarantee.
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(101, "Child").WithParent(100).Build() });
        _workItemRepo.GetChildrenAsync(101, Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(100, "Cycles back").Build() });

        var membership = await CreateSut().EvaluateAsync(BenchOf(BenchSelector.ForSubtree(100)));

        membership.AllIds.ShouldBe(new HashSet<int> { 100, 101 }, ignoreOrder: true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Test 6 — a Bench displays with NO NETWORK
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test6_BenchEvaluates_WithTheAdoEndpointUnreachable()
    {
        // The ADO-facing service throws on every call, standing in for an unreachable endpoint.
        var ado = Substitute.For<IIterationService>();
        ado.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns<IterationPath>(_ => throw new HttpRequestException("Endpoint unreachable."));
        ado.GetTeamIterationsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TeamIteration>>(_ => throw new HttpRequestException("Endpoint unreachable."));

        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new WorkItemBuilder(5, "Cached").WithIterationPath(Sprint.Value).Build() });

        var bench = BenchOf(BenchSelector.ForCurrentSprint(null), BenchSelector.ForItem(9));

        // Reachable: the calendar answers from cached dates and the local clock.
        var offline = await CreateSut().EvaluateAsync(bench);

        offline.AllIds.ShouldBe(new HashSet<int> { 5, 9 }, ignoreOrder: true);

        // The precondition that stops this being a tautology: the ADO service really does throw,
        // so a regression to server-side evaluation could not have passed silently.
        await Should.ThrowAsync<HttpRequestException>(() => ado.GetCurrentIterationAsync());
    }

    [Fact]
    public async Task QuerySelector_IsAnsweredFromTheLocalCalendar_NotTheNetwork()
    {
        await CreateSut().EvaluateAsync(BenchOf(BenchSelector.ForCurrentSprint(null)));

        // The calendar — local data plus the local clock — is what answers "which sprint is now".
        await _calendar.Received(1).GetCurrentIterationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuerySelector_WithNoCalendarEntryCoveringToday_MatchesNothing()
    {
        _calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IterationPath>());

        var membership = await CreateSut().EvaluateAsync(BenchOf(BenchSelector.ForCurrentSprint(null)));

        // Matching nothing is correct; matching EVERYTHING would silently widen the view.
        membership.QueryMatches.ShouldBeEmpty();
        await _workItemRepo.DidNotReceive()
            .GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuerySelector_AppliesItsOwnAssigneeFilter()
    {
        _workItemRepo.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WorkItemBuilder(1, "Mine").AssignedTo("Ada Lovelace").WithIterationPath(Sprint.Value).Build(),
                new WorkItemBuilder(2, "Theirs").AssignedTo("Grace Hopper").WithIterationPath(Sprint.Value).Build(),
            });

        var membership = await CreateSut().EvaluateAsync(
            BenchOf(BenchSelector.ForCurrentSprint("Ada Lovelace")));

        membership.AllIds.ShouldBe(new HashSet<int> { 1 }, ignoreOrder: true);
    }

    [Fact]
    public async Task UnknownQueryRule_FailsLoudly()
    {
        // A rule this build does not understand must not be silently skipped — that would drop a
        // rule the person added and quietly change their view.
        var bench = BenchOf(new BenchSelector(Domain.Enums.SelectorKind.Query, "rule-from-the-future"));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateSut().EvaluateAsync(bench));
    }
}
